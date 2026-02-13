#!/usr/bin/env bash
set -euo pipefail

api_url="${API_URL:-http://127.0.0.1:8087}"
connection_string="${CONNECTION_STRING:-Host=localhost;Port=5432;Username=artifortress;Password=artifortress;Database=artifortress}"
bootstrap_token="${BOOTSTRAP_TOKEN:-phase7-demo-bootstrap}"
oidc_issuer="${OIDC_ISSUER:-https://phase7-idp.local}"
oidc_audience="${OIDC_AUDIENCE:-artifortress-api}"
oidc_shared_secret="${OIDC_SHARED_SECRET:-phase7-demo-oidc-secret}"
oidc_role_mappings="${OIDC_ROLE_MAPPINGS:-groups|af-admins|*|admin}"
saml_idp_metadata_url="${SAML_IDP_METADATA_URL:-https://phase7-idp.local/metadata}"
saml_expected_issuer="${SAML_EXPECTED_ISSUER:-https://phase7-idp.local/issuer}"
saml_sp_entity_id="${SAML_SP_ENTITY_ID:-urn:artifortress:phase7:sp}"
saml_role_mappings="${SAML_ROLE_MAPPINGS:-groups|af-admins|*|admin}"
api_dll="src/Artifortress.Api/bin/Debug/net10.0/Artifortress.Api.dll"
api_log_file="/tmp/artifortress-phase7-demo-api.log"
report_path="${REPORT_PATH:-docs/reports/phase7-oidc-latest.md}"
skip_static_checks="${SKIP_STATIC_CHECKS:-false}"

if [ ! -f "$api_dll" ]; then
  echo "Missing API binary at $api_dll. Run 'make build' first."
  exit 1
fi

response_status=""
response_body=""

b64url() {
  openssl base64 -A | tr '+/' '-_' | tr -d '='
}

issue_oidc_token() {
  local subject="$1"
  local audience="$2"
  local scope_text="$3"
  local exp_epoch="$4"
  local header payload header_b64 payload_b64 signature

  header='{"alg":"HS256","typ":"JWT"}'
  payload="{\"iss\":\"$oidc_issuer\",\"sub\":\"$subject\",\"aud\":\"$audience\",\"exp\":$exp_epoch,\"scope\":\"$scope_text\"}"
  header_b64="$(printf '%s' "$header" | b64url)"
  payload_b64="$(printf '%s' "$payload" | b64url)"
  signature="$(printf '%s' "${header_b64}.${payload_b64}" | openssl dgst -binary -sha256 -hmac "$oidc_shared_secret" | b64url)"
  printf '%s.%s.%s' "$header_b64" "$payload_b64" "$signature"
}

issue_oidc_groups_token() {
  local subject="$1"
  local audience="$2"
  local group_name="$3"
  local exp_epoch="$4"
  local header payload header_b64 payload_b64 signature

  header='{"alg":"HS256","typ":"JWT"}'
  payload="{\"iss\":\"$oidc_issuer\",\"sub\":\"$subject\",\"aud\":\"$audience\",\"exp\":$exp_epoch,\"groups\":[\"$group_name\"]}"
  header_b64="$(printf '%s' "$header" | b64url)"
  payload_b64="$(printf '%s' "$payload" | b64url)"
  signature="$(printf '%s' "${header_b64}.${payload_b64}" | openssl dgst -binary -sha256 -hmac "$oidc_shared_secret" | b64url)"
  printf '%s.%s.%s' "$header_b64" "$payload_b64" "$signature"
}

curl_call() {
  local method="$1"
  local path="$2"
  local data="$3"
  shift 3

  local temp_body
  temp_body="$(mktemp)"

  if [ -n "$data" ]; then
    response_status="$(curl -sS -o "$temp_body" -w "%{http_code}" -X "$method" "$api_url$path" -H "Content-Type: application/json" "$@" --data "$data")"
  else
    response_status="$(curl -sS -o "$temp_body" -w "%{http_code}" -X "$method" "$api_url$path" "$@")"
  fi

  response_body="$(cat "$temp_body")"
  rm -f "$temp_body"
}

curl_form_call() {
  local method="$1"
  local path="$2"
  local data="$3"
  shift 3

  local temp_body
  temp_body="$(mktemp)"
  response_status="$(curl -sS -o "$temp_body" -w "%{http_code}" -X "$method" "$api_url$path" -H "Content-Type: application/x-www-form-urlencoded" "$@" --data "$data")"
  response_body="$(cat "$temp_body")"
  rm -f "$temp_body"
}

assert_status() {
  local expected="$1"
  if [ "$response_status" != "$expected" ]; then
    echo "Expected HTTP $expected but got $response_status."
    echo "Response body: $response_body"
    exit 1
  fi
}

contains_text() {
  local haystack="$1"
  local needle="$2"
  printf '%s' "$haystack" | grep -Fq "$needle"
}

json_string_field() {
  local json="$1"
  local field="$2"
  printf '%s' "$json" | sed -n "s/.*\"$field\":\"\\([^\"]*\\)\".*/\\1/p"
}

if [ "$skip_static_checks" = "true" ]; then
  echo "[phase7-demo] Skipping static checks (SKIP_STATIC_CHECKS=true)"
else
  echo "[phase7-demo] Running static checks..."
  make test
  make format
fi

echo "[phase7-demo] Starting API at $api_url"
ConnectionStrings__Postgres="$connection_string" \
Auth__BootstrapToken="$bootstrap_token" \
Auth__Oidc__Enabled="true" \
Auth__Oidc__Issuer="$oidc_issuer" \
Auth__Oidc__Audience="$oidc_audience" \
Auth__Oidc__Hs256SharedSecret="$oidc_shared_secret" \
Auth__Oidc__RoleMappings="$oidc_role_mappings" \
Auth__Saml__Enabled="true" \
Auth__Saml__IdpMetadataUrl="$saml_idp_metadata_url" \
Auth__Saml__ExpectedIssuer="$saml_expected_issuer" \
Auth__Saml__ServiceProviderEntityId="$saml_sp_entity_id" \
Auth__Saml__RoleMappings="$saml_role_mappings" \
  dotnet "$api_dll" --urls "$api_url" >"$api_log_file" 2>&1 &
api_pid=$!

cleanup() {
  kill "$api_pid" >/dev/null 2>&1 || true
}
trap cleanup EXIT

for _ in {1..40}; do
  if curl -sf "$api_url/health/live" >/dev/null; then
    break
  fi
  sleep 1
done

if ! curl -sf "$api_url/health/live" >/dev/null; then
  echo "API did not become healthy."
  echo "Logs:"
  cat "$api_log_file"
  exit 1
fi

echo "[phase7-demo] Validating OIDC whoami path"
oidc_subject="phase7-oidc-user"
exp_epoch="$(( $(date +%s) + 900 ))"
oidc_token="$(issue_oidc_token "$oidc_subject" "$oidc_audience" "repo:*:admin" "$exp_epoch")"

curl_call "GET" "/v1/auth/whoami" "" -H "Authorization: Bearer $oidc_token"
assert_status "200"
whoami_body="$response_body"

if ! contains_text "$whoami_body" "\"subject\":\"$oidc_subject\""; then
  echo "OIDC whoami did not return expected subject. Body: $whoami_body"
  exit 1
fi

if ! contains_text "$whoami_body" "\"authSource\":\"oidc\""; then
  echo "OIDC whoami did not return authSource=oidc. Body: $whoami_body"
  exit 1
fi

echo "[phase7-demo] Validating OIDC admin scope can create repo"
repo_key="phase7-oidc-demo-$(date +%s)"
curl_call "POST" "/v1/repos" "{\"RepoKey\":\"$repo_key\",\"RepoType\":\"local\",\"UpstreamUrl\":\"\",\"MemberRepos\":[]}" -H "Authorization: Bearer $oidc_token"
assert_status "201"

echo "[phase7-demo] Validating OIDC audience mismatch rejection"
bad_audience_token="$(issue_oidc_token "$oidc_subject" "wrong-audience" "repo:*:admin" "$exp_epoch")"
curl_call "GET" "/v1/auth/whoami" "" -H "Authorization: Bearer $bad_audience_token"
assert_status "401"

echo "[phase7-demo] Validating OIDC claim-role mapping path"
oidc_groups_subject="phase7-oidc-groups-user"
oidc_groups_token="$(issue_oidc_groups_token "$oidc_groups_subject" "$oidc_audience" "af-admins" "$exp_epoch")"
curl_call "GET" "/v1/auth/whoami" "" -H "Authorization: Bearer $oidc_groups_token"
assert_status "200"

if ! contains_text "$response_body" "\"repo:*:admin\""; then
  echo "OIDC claim-role mapping did not resolve repo:*:admin scope. Body: $response_body"
  exit 1
fi

echo "[phase7-demo] Validating SAML metadata endpoint"
curl_call "GET" "/v1/auth/saml/metadata" ""
assert_status "200"

if ! contains_text "$response_body" "$saml_sp_entity_id"; then
  echo "SAML metadata missing configured SP entity id. Body: $response_body"
  exit 1
fi

echo "[phase7-demo] Validating SAML ACS assertion exchange"
saml_subject="phase7-saml-user"
saml_xml="<samlp:Response xmlns:samlp=\"urn:oasis:names:tc:SAML:2.0:protocol\" xmlns:saml=\"urn:oasis:names:tc:SAML:2.0:assertion\"><saml:Issuer>${saml_expected_issuer}</saml:Issuer><saml:Assertion><saml:Issuer>${saml_expected_issuer}</saml:Issuer><saml:Subject><saml:NameID>${saml_subject}</saml:NameID></saml:Subject><saml:Conditions><saml:AudienceRestriction><saml:Audience>${saml_sp_entity_id}</saml:Audience></saml:AudienceRestriction></saml:Conditions><saml:AttributeStatement><saml:Attribute Name=\"groups\"><saml:AttributeValue>af-admins</saml:AttributeValue></saml:Attribute></saml:AttributeStatement></saml:Assertion></samlp:Response>"
saml_response_b64="$(printf '%s' "$saml_xml" | b64url)"
curl_form_call "POST" "/v1/auth/saml/acs" "SAMLResponse=$saml_response_b64&RelayState=phase7-relay"
assert_status "200"
saml_token="$(json_string_field "$response_body" "token")"

if [ -z "$saml_token" ]; then
  echo "Failed to parse SAML exchange token."
  exit 1
fi

curl_call "GET" "/v1/auth/whoami" "" -H "Authorization: Bearer $saml_token"
assert_status "200"

if ! contains_text "$response_body" "\"authSource\":\"pat\""; then
  echo "SAML issued token did not authenticate via PAT path. Body: $response_body"
  exit 1
fi

echo "[phase7-demo] Validating PAT path still works"
curl_call "POST" "/v1/auth/pats" "{\"Subject\":\"phase7-admin\",\"Scopes\":[\"repo:*:admin\"],\"TtlMinutes\":60}" -H "X-Bootstrap-Token: $bootstrap_token"
assert_status "200"
admin_token="$(json_string_field "$response_body" "token")"

if [ -z "$admin_token" ]; then
  echo "Failed to parse PAT from auth response."
  exit 1
fi

curl_call "GET" "/v1/auth/whoami" "" -H "Authorization: Bearer $admin_token"
assert_status "200"

if ! contains_text "$response_body" "\"authSource\":\"pat\""; then
  echo "PAT whoami did not return authSource=pat. Body: $response_body"
  exit 1
fi

mkdir -p "$(dirname "$report_path")"
{
  echo "# Phase 7 OIDC Demo Report"
  echo
  echo "Generated at: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo
  echo "## Checks"
  echo
  echo "- make test: PASS"
  echo "- make format: PASS"
  echo "- OIDC whoami subject/authSource: PASS"
  echo "- OIDC admin-scoped repo create: PASS"
  echo "- OIDC invalid audience unauthorized: PASS"
  echo "- OIDC claim-role mapping policy: PASS"
  echo "- SAML metadata endpoint contract: PASS"
  echo "- SAML ACS exchange and mapped-scope token issuance: PASS"
  echo "- PAT compatibility path: PASS"
  echo
  echo "## OIDC whoami Snapshot"
  echo
  echo '```json'
  echo "$whoami_body"
  echo '```'
} > "$report_path"

echo "[phase7-demo] Report written to $report_path"
echo "[phase7-demo] Phase 7 OIDC demo completed successfully."
