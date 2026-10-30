#!/usr/bin/env bash
# Walks through the group pre-order owner endpoints (homework Levels 1-3) against the seeded demo data.
# Prompts for your login; the password and token stay in this shell and are never printed.
#
# Usage: bash backend/restaurant-backend-app/scripts/try-group-preorder.sh
# Note: the submit step changes the data. Re-run the seed to reset it before running this again.
set -euo pipefail

API_URL="${API_URL:-https://8nqprjldl4.execute-api.eu-north-1.amazonaws.com/develop}"
GROUP_ID="seed-group-1"
RESERVATION_ID="seed-reservation-1"

read -rp "Email: " EMAIL
read -rsp "Password: " PASSWORD
echo

TOKEN=$(curl -s -X POST "$API_URL/signin" -H "Content-Type: application/json" \
  -d "$(python3 -c 'import json,sys; print(json.dumps({"email": sys.argv[1], "password": sys.argv[2]}))' "$EMAIL" "$PASSWORD")" \
  | python3 -c 'import json,sys; print(json.load(sys.stdin).get("idToken", ""))')
unset PASSWORD

if [ -z "$TOKEN" ]; then
  echo "Sign-in failed: check the email and password." >&2
  exit 1
fi

step() {
  local title="$1" expected="$2" method="$3" path="$4" body="${5:-}"
  echo
  echo "== $title"
  echo "   $method $path"
  echo "   expected: $expected"
  local args=(-s -w '\n%{http_code}' -X "$method" "$API_URL$path" -H "Authorization: Bearer $TOKEN")
  [ -n "$body" ] && args+=(-H "Content-Type: application/json" -d "$body")
  local response status
  response=$(curl "${args[@]}")
  status=$(tail -n1 <<<"$response")
  echo "   got:      $status"
  sed '$d' <<<"$response" | python3 -c '
import json, sys
raw = sys.stdin.read()
try:
    data = json.loads(raw)
except ValueError:
    print("   " + raw); sys.exit()
if isinstance(data, dict) and "participants" in data:
    for p in data["participants"]:
        dishes = ", ".join("%s x%s" % (i["dish"]["name"], i["quantity"]) for i in p["items"])
        print("   %-5s %-8s total=%-5s submitted=%-5s [%s]" % (p["type"], p["name"], p["total"], p["isSubmitted"], dishes))
    print("   grand total = %s" % data["total"])
else:
    print("   " + json.dumps(data))
'
}

step "Level 1: group snapshot" \
  "200, Owner 260 + Andrii 360 + Olena 370 + Max 620, grand total 1610" \
  GET "/guest/groups/$GROUP_ID/sessions"

step "Level 2: owner's guest session id" \
  "200, guestSessionId = seed-session-owner" \
  GET "/reservations/$RESERVATION_ID/guest-session-id"

step "Level 3: remove Olena" \
  "200, Olena gone, grand total 1240" \
  DELETE "/guest/groups/$GROUP_ID/sessions/seed-session-olena"

step "Level 3: removing the owner's own session is refused" \
  "400" \
  DELETE "/guest/groups/$GROUP_ID/sessions/seed-session-owner"

step "Level 3: removing a session that doesn't exist" \
  "404 (no such session)" \
  DELETE "/guest/groups/$GROUP_ID/sessions/does-not-exist"

step "Level 2: submit the group pre-order" \
  "200, Caesar 3, Tom Yum 2, Pasta 1" \
  POST "/group-reservations/$GROUP_ID/submit" "{\"reservationId\": \"$RESERVATION_ID\"}"

step "Level 2: submitting twice is refused" \
  "409" \
  POST "/group-reservations/$GROUP_ID/submit" "{\"reservationId\": \"$RESERVATION_ID\"}"

step "Level 3: removing a guest after submit is refused" \
  "400" \
  DELETE "/guest/groups/$GROUP_ID/sessions/seed-session-andrii"

step "Level 2: unknown group" \
  "404" \
  POST "/group-reservations/no-such-group/submit" "{\"reservationId\": \"$RESERVATION_ID\"}"
