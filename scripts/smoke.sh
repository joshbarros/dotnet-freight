#!/usr/bin/env bash
# Drives the composed stack over HTTP. Every code carries a run id, so a kept Postgres volume never collides.
# Needs: curl, python3. The stack must be up: docker compose up --build --wait
set -euo pipefail

API="${API:-http://127.0.0.1:5210}"
RABBIT="${RABBIT:-http://127.0.0.1:15700}"
RUN="$(date +%s)$((RANDOM % 900 + 100))"
JSON='content-type: application/json'

say() { printf '  %s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }
jget() { python3 -c "import sys,json; d=json.load(sys.stdin); print(d$1)"; }

open_sailing() { # code capacity -> prints nothing
  curl -sf -X POST "$API/api/sailings" -H "$JSON" \
    -d "{\"code\":\"$1\",\"vessel\":\"MV Smoke\",\"origin\":\"Santos\",\"destination\":\"Rotterdam\",\"capacityTeu\":$2}" >/dev/null \
    || fail "could not open sailing $1"
}
book() { # code reference teu -> prints booking id
  curl -sf -X POST "$API/api/bookings" -H "$JSON" \
    -d "{\"sailingCode\":\"$1\",\"reference\":\"$2\",\"teu\":$3}" | jget "['id']"
}
booking_field() { curl -sf "$API/api/bookings/$1" | jget "['$2']"; }
remaining() { curl -sf "$API/api/sailings/$1" | jget "['remainingTeu']"; }

wait_for() { # description command... : polls up to 30s until the command succeeds
  local what="$1"; shift
  for _ in $(seq 1 60); do
    if "$@" >/dev/null 2>&1; then return 0; fi
    sleep 0.5
  done
  fail "timed out waiting for $what"
}
status_is() { [ "$(booking_field "$1" status)" = "$2" ]; }
remaining_is() { [ "$(remaining "$1")" = "$2" ]; }
terminal() { case "$(booking_field "$1" status)" in Issued|Rejected|Reversed) return 0 ;; *) return 1 ;; esac; }
event_types() { curl -sf "$API/api/bookings/$1/events" | python3 -c "import sys,json; print(' '.join(e['type'] for e in json.load(sys.stdin)))"; }
dlq_is() { [ "$(dlq_count)" = "$1" ]; }
dlq_count() { curl -sf -u platform:platform "$RABBIT/api/queues/%2F/documents.work.dlq" | jget "['messages']"; }

curl -sf "$API/api/health" >/dev/null || fail "API is not healthy at $API; run: docker compose up --build --wait"

echo "1. input is validated at the door"
open_sailing "V-$RUN" 10
for body in \
  "{\"sailingCode\":\"V-$RUN\",\"reference\":\"\",\"teu\":1}" \
  "{\"sailingCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"teu\":0}" \
  "{\"sailingCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"teu\":21}" \
  "{\"sailingCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"teu\":4294967297}" \
  "{\"sailingCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"teu\":9223372036854775807}"; do
  code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/api/bookings" -H "$JSON" -d "$body")"
  [ "$code" = 400 ] || fail "expected 400 for $body, got $code"
done
say "empty, zero, over-limit and overflowing TEU are all 400"

echo "2. happy path"
open_sailing "H-$RUN" 10
ID="$(book "H-$RUN" "BK-$RUN-1" 3)"
wait_for "booking to be Issued" status_is "$ID" Issued
[ "$(booking_field "$ID" feeCents)" = 277500 ] || fail "fee should be 3 x 90000 + 7500 cents"
[ "$(curl -sf "$API/api/documents/$ID" | jget "['status']")" = Issued ] || fail "bill of lading not issued"
wait_for "remaining TEU to be 7" remaining_is "H-$RUN" 7
TYPES="$(event_types "$ID")"
for t in capacity.reserve capacity.held document.issued; do
  case "$TYPES" in *"$t"*) ;; *) fail "Kafka log is missing $t (has: $TYPES)" ;; esac
done
say "issued, fee 277500, remaining 7, Kafka log has the three steps"

echo "3. last unit: three bookings, one commits, two are short and write no hold"
open_sailing "L-$RUN" 1
TMP="$(mktemp -d)"
for n in A B C; do ( book "L-$RUN" "BK-$RUN-$n" 1 >"$TMP/$n" ) & done
wait
IDS=""
for n in A B C; do [ -s "$TMP/$n" ] || fail "booking $n was not accepted"; IDS="$IDS $(cat "$TMP/$n")"; done
rm -rf "$TMP"
for i in $IDS; do wait_for "booking $i to settle" terminal "$i"; done
ISSUED=0; SHORT=0
for i in $IDS; do
  s="$(booking_field "$i" status)"
  if [ "$s" = Issued ]; then ISSUED=$((ISSUED + 1)); fi
  if [ "$s" = Rejected ]; then
    case "$(booking_field "$i" reason)" in Short*) SHORT=$((SHORT + 1)); case "$(event_types "$i")" in *capacity.held*) fail "the short loser has a hold" ;; esac ;; esac
  fi
done
[ "$ISSUED" = 1 ] && [ "$SHORT" = 2 ] || fail "expected 1 issued and 2 short, got $ISSUED issued and $SHORT short"
[ "$(remaining "L-$RUN")" = 0 ] || fail "remaining should be 0"
say "1 issued, 2 short, remaining 0, no hold for the losers"

echo "4. poison: committed as rejected, dead-lettered, the TEU comes back"
open_sailing "P-$RUN" 5
BEFORE="$(dlq_count)"
IDP="$(book "P-$RUN" "BK-POISON-$RUN" 2)"
wait_for "poison booking to be Rejected" status_is "$IDP" Rejected
wait_for "TEU to be restored to 5" remaining_is "P-$RUN" 5
wait_for "documents.work.dlq to gain one message" dlq_is $((BEFORE + 1))
[ "$(curl -sf "$API/api/documents/$IDP" | jget "['status']")" = Rejected ] || fail "rejection should be committed on the document row"
[ "$(booking_field "$IDP" capacityReleasedAt)" != None ] || fail "booking should record the release"
say "rejected, dead-lettered (+1 in documents.work.dlq), remaining back to 5"

echo "5. reversal races the final mark: the reversal wins, the issued document stays"
open_sailing "R-$RUN" 6
WON=0
for attempt in 1 2 3; do
  IDR="$(book "R-$RUN" "BK-$RUN-R$attempt" 2)"
  # The bill of lading exists before the booking is marked Issued (the final mark is delayed in compose).
  wait_for "bill of lading to exist" curl -sf "$API/api/documents/$IDR"
  if status_is "$IDR" Held; then WON=1; break; fi
  say "window missed on attempt $attempt (booking already Issued); the reversal still has to hold; retrying"
  curl -sf -X POST "$API/api/bookings/$IDR/reversal" >/dev/null || true
  wait_for "booking to be Reversed" status_is "$IDR" Reversed
done
[ "$WON" = 1 ] || fail "never caught the booking between document issued and final mark"
curl -sf -X POST "$API/api/bookings/$IDR/reversal" >/dev/null || fail "reversal was refused"
wait_for "booking to be Reversed" status_is "$IDR" Reversed
sleep 3 # longer than the final-mark delay: the late mark must not undo the reversal
status_is "$IDR" Reversed || fail "the final mark overwrote the reversal"
[ "$(curl -sf "$API/api/documents/$IDR" | jget "['status']")" = Issued ] || fail "the issued document was rolled back"
[ "$(booking_field "$IDR" billOfLadingNumber)" = None ] || fail "the late final mark should have been skipped"
wait_for "TEU to come back" remaining_is "R-$RUN" 6
code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/api/bookings/$IDR/reversal")"
[ "$code" = 409 ] || fail "second reversal should be 409, got $code"
say "booking Reversed, document still Issued, remaining 6, second reversal 409"

echo "freight smoke passed"
