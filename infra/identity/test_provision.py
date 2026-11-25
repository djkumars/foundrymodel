import argparse
import contextlib
import io
import unittest
from unittest.mock import patch

import provision

TENANT = "00000000-0000-0000-0000-000000000001"
API_OBJECT = "00000000-0000-0000-0000-000000000002"
API_APP = "00000000-0000-0000-0000-000000000003"
SPA_OBJECT = "00000000-0000-0000-0000-000000000004"
SPA_APP = "00000000-0000-0000-0000-000000000005"
CALLBACK = "https://chat.example.com/auth-callback.html"
ARGS = ["--tenant-id", TENANT, "--tenant-label", "exampletenant",
        "--api-name", "API", "--spa-name", "SPA", "--redirect-uri", CALLBACK]


class FakeGraph:
    def __init__(self, wrong_tenant=False, existing=False):
        self.calls = []
        self.wrong_tenant = wrong_tenant
        self.existing = existing

    def request(self, method, path, body=None):
        self.calls.append((method, path, body))
        if path.startswith("/organization"):
            return {"value": [{"id": SPA_APP if self.wrong_tenant else TENANT,
                               "verifiedDomains": [{"isInitial": True,
                                                    "name": "exampletenant.onmicrosoft.com"}]}]}
        if method == "GET":
            return {"value": [{"id": API_OBJECT}] if self.existing else []}
        if method == "POST" and path == "/applications":
            if body["displayName"] == "API":
                return {"id": API_OBJECT, "appId": API_APP}
            return {"id": SPA_OBJECT, "appId": SPA_APP}
        return {}


class ProvisionTests(unittest.TestCase):
    def test_plan_is_offline(self):
        with patch.object(provision, "Graph", side_effect=AssertionError("network")), \
                contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(provision.main(ARGS), 0)

    def test_reject_bad_redirects(self):
        for uri in ["http://chat.example.com/auth-callback.html",
                    "https://*.example.com/auth-callback.html",
                    "https://chat.example.com/auth-callback.html?x=1",
                    "https://user@chat.example.com/auth-callback.html",
                    "https://chat.example.com/other",
                    "http://localhost:4201/auth-callback.html"]:
            with self.subTest(uri=uri), self.assertRaises(argparse.ArgumentTypeError):
                provision.redirect_uri(uri)
        self.assertEqual(provision.redirect_uri("http://localhost:4200/auth-callback.html"),
                         "http://localhost:4200/auth-callback.html")

    def test_writes_and_output_match_app_contract(self):
        graph = FakeGraph()
        args = unittest.mock.Mock(tenant_id=TENANT, tenant_label="exampletenant",
                                  api_name="API", spa_name="SPA", redirect_uri=[CALLBACK])
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            provision.provision(graph, args)
        writes = [c for c in graph.calls if c[0] != "GET"]
        self.assertEqual([c[0] for c in writes], ["POST", "PATCH", "POST", "POST", "POST"])
        api_patch = writes[1][2]
        self.assertEqual(api_patch["identifierUris"], ["api://" + API_APP])
        self.assertEqual(api_patch["api"]["requestedAccessTokenVersion"], 2)
        scope = api_patch["api"]["oauth2PermissionScopes"][0]
        self.assertEqual(scope["value"], "access_as_user")
        spa = writes[3][2]
        self.assertEqual(spa["spa"]["redirectUris"], [CALLBACK])
        self.assertEqual(spa["requiredResourceAccess"][0]["resourceAccess"][0]["id"], scope["id"])
        self.assertIn(f"https://exampletenant.ciamlogin.com/{TENANT}/v2.0", output.getvalue())
        self.assertIn(f"api://{API_APP}/access_as_user", output.getvalue())

    def test_wrong_tenant_or_existing_name_fail_before_write(self):
        args = unittest.mock.Mock(tenant_id=TENANT, tenant_label="exampletenant",
                                  api_name="API", spa_name="SPA", redirect_uri=[CALLBACK])
        for graph in [FakeGraph(wrong_tenant=True), FakeGraph(existing=True)]:
            with self.assertRaises(RuntimeError):
                provision.provision(graph, args)
            self.assertTrue(all(call[0] == "GET" for call in graph.calls))

    def test_wrong_initial_domain_fails(self):
        graph = FakeGraph()
        with self.assertRaises(RuntimeError):
            provision.assert_tenant(graph, TENANT, "wrong")

    def test_graph_redirects_are_not_followed(self):
        self.assertIsNone(provision.NoRedirect().redirect_request(
            None, None, 302, "Found", {}, "https://other.example/"))

    def test_failure_after_api_creation_does_not_create_spa(self):
        class FailPatch(FakeGraph):
            def request(self, method, path, body=None):
                if method == "PATCH":
                    self.calls.append((method, path, body))
                    raise RuntimeError("simulated failure")
                return super().request(method, path, body)

        graph = FailPatch()
        args = unittest.mock.Mock(tenant_id=TENANT, tenant_label="exampletenant",
                                  api_name="API", spa_name="SPA", redirect_uri=[CALLBACK])
        with contextlib.redirect_stdout(io.StringIO()), self.assertRaises(RuntimeError):
            provision.provision(graph, args)
        self.assertEqual(len([c for c in graph.calls if c[0] == "POST"]), 1)


if __name__ == "__main__":
    unittest.main()
