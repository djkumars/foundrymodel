#!/usr/bin/env python3
"""Create a fresh pair of External ID app registrations; see README before use."""

import argparse
import getpass
import json
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid

GRAPH = "https://graph.microsoft.com/v1.0"
CALLBACK = "/auth-callback.html"


def guid(value):
    try:
        return str(uuid.UUID(value))
    except ValueError as exc:
        raise argparse.ArgumentTypeError("expected a GUID") from exc


def tenant_label(value):
    value = value.lower()
    if not re.fullmatch(r"[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?", value):
        raise argparse.ArgumentTypeError("expected the initial tenant domain label")
    return value


def redirect_uri(value):
    try:
        u = urllib.parse.urlsplit(value)
        host, port = u.hostname, u.port
    except ValueError as exc:
        raise argparse.ArgumentTypeError("invalid redirect URI") from exc
    if (not host or u.username or u.password or u.query or u.fragment or
            u.path != CALLBACK or "*" in value):
        raise argparse.ArgumentTypeError("redirect URI must be an exact origin plus " + CALLBACK)
    if value == "http://localhost:4200" + CALLBACK:
        return value
    if (u.scheme != "https" or host == "localhost" or ":" in host or
            not re.fullmatch(r"[a-zA-Z0-9.-]+", host) or "." not in host or
            any(not re.fullmatch(r"[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?", label)
                for label in host.split(".")) or (port is not None and port != 443)):
        raise argparse.ArgumentTypeError("use an HTTPS DNS origin, or the exact localhost development URI")
    return value


def app_name(value):
    if not value.strip() or len(value) > 256 or any(ord(c) < 32 for c in value):
        raise argparse.ArgumentTypeError("app display name must be 1–256 printable characters")
    return value


def api_create(name):
    return {"displayName": name, "signInAudience": "AzureADMyOrg"}


def api_patch(app_id, scope_id):
    return {
        "identifierUris": ["api://" + app_id],
        "api": {
            "requestedAccessTokenVersion": 2,
            "oauth2PermissionScopes": [{
                "id": scope_id, "value": "access_as_user", "type": "User",
                "isEnabled": True,
                "adminConsentDisplayName": "Access the chatbot API",
                "adminConsentDescription": "Access the chatbot API as the signed-in user.",
                "userConsentDisplayName": "Access the chatbot API",
                "userConsentDescription": "Access the chatbot API as you.",
            }],
        },
    }


def spa_create(name, redirects, api_id, scope_id):
    return {
        "displayName": name,
        "signInAudience": "AzureADMyOrg",
        "spa": {"redirectUris": redirects},
        "requiredResourceAccess": [{
            "resourceAppId": api_id,
            "resourceAccess": [{"id": scope_id, "type": "Scope"}],
        }],
    }


class Graph:
    def __init__(self, token):
        self.token = token

    def request(self, method, path, body=None):
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(
            GRAPH + path, data=data, method=method,
            headers={"Authorization": "Bearer " + self.token,
                     "Content-Type": "application/json", "Accept": "application/json"},
        )
        try:
            # Never forward a bearer token through an HTTP redirect.
            with urllib.request.build_opener(NoRedirect).open(req, timeout=30) as response:
                content = response.read()
                return json.loads(content) if content else {}
        except urllib.error.HTTPError as exc:
            # Do not log request headers, tokens, or server error bodies.
            raise RuntimeError(f"Microsoft Graph {method} failed with HTTP {exc.code}") from None
        except (urllib.error.URLError, TimeoutError) as exc:
            raise RuntimeError(f"Microsoft Graph {method} failed: network error; outcome may be unknown") from exc


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def assert_tenant(graph, tenant_id, label):
    result = graph.request("GET", "/organization?$select=id,verifiedDomains")
    organizations = result.get("value", [])
    if len(organizations) != 1 or organizations[0].get("id", "").lower() != tenant_id:
        raise RuntimeError("Graph token is not for the requested tenant")
    domains = organizations[0].get("verifiedDomains") or []
    if not any(d.get("isInitial") and d.get("name", "").lower() == label + ".onmicrosoft.com"
               for d in domains):
        raise RuntimeError("tenant label does not match the tenant's initial verified domain")


def check_name_unused(graph, name):
    # Exact-name safety guard, not an idempotency mechanism or concurrency lock.
    filter_value = "displayName eq '" + name.replace("'", "''") + "'"
    query = urllib.parse.urlencode({"$filter": filter_value, "$select": "id,displayName"})
    result = graph.request("GET", "/applications?" + query)
    if result.get("value") or result.get("@odata.nextLink"):
        raise RuntimeError(f"Application name already exists: {name!r}; stop and inspect the tenant")


def provision(graph, args):
    assert_tenant(graph, args.tenant_id, args.tenant_label)
    check_name_unused(graph, args.api_name)
    check_name_unused(graph, args.spa_name)
    scope_id = str(uuid.uuid4())
    api = graph.request("POST", "/applications", api_create(args.api_name))
    api_obj, api_id = str(uuid.UUID(api["id"])), str(uuid.UUID(api["appId"]))
    print(f"Created API: object ID {api_obj}; application ID {api_id}", flush=True)
    graph.request("PATCH", "/applications/" + api_obj, api_patch(api_id, scope_id))
    graph.request("POST", "/servicePrincipals", {"appId": api_id})
    print("Created API service principal.", flush=True)
    spa = graph.request("POST", "/applications",
                        spa_create(args.spa_name, args.redirect_uri, api_id, scope_id))
    spa_obj, spa_id = str(uuid.UUID(spa["id"])), str(uuid.UUID(spa["appId"]))
    print(f"Created SPA: object ID {spa_obj}; application ID {spa_id}", flush=True)
    graph.request("POST", "/servicePrincipals", {"appId": spa_id})
    print("Created SPA service principal.", flush=True)
    authority = f"https://{args.tenant_label}.ciamlogin.com/{args.tenant_id}"
    print("\nFrontend auth-config.json:")
    print(json.dumps({"clientId": spa_id, "authority": authority,
                      "apiScope": f"api://{api_id}/access_as_user"}, indent=2))
    print("\nAPI settings:")
    print(json.dumps({"ExternalId__TenantId": args.tenant_id,
                      "ExternalId__Authority": authority + "/v2.0",
                      "ExternalId__Audience": api_id}, indent=2))
    print("\nManual steps remain: user flow, SPA association, consent, and live validation.")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tenant-id", required=True, type=guid)
    parser.add_argument("--tenant-label", required=True, type=tenant_label)
    parser.add_argument("--redirect-uri", action="append", required=True, type=redirect_uri)
    parser.add_argument("--api-name", required=True, type=app_name)
    parser.add_argument("--spa-name", required=True, type=app_name)
    parser.add_argument("--apply", action="store_true", help="explicitly enable Graph writes")
    args = parser.parse_args(argv)
    if args.api_name.casefold() == args.spa_name.casefold():
        parser.error("API and SPA names must differ")
    if len(set(args.redirect_uri)) != len(args.redirect_uri):
        parser.error("duplicate redirect URI")
    if not args.apply:
        print("Offline plan only: validate tenant and names, create API and SPA registrations and service principals.")
        print("Redirect URIs:", ", ".join(args.redirect_uri))
        print("No network calls or changes. Re-run with --apply only after reading README.md.")
        return 0
    token = getpass.getpass("Paste Microsoft Graph delegated access token (not echoed): ").strip()
    if not token:
        raise RuntimeError("No Graph access token supplied")
    provision(Graph(token), args)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (RuntimeError, KeyError, ValueError) as exc:
        print(f"Stopped: {exc}. Inspect the tenant for partial resources before retrying.", file=sys.stderr)
        sys.exit(1)
