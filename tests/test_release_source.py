"""Exercise the actual release workflow guard with recorded-style API fixtures."""
import base64
import contextlib
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import textwrap
import unittest
from unittest.mock import patch


REPO = "kurasis/FindCopy"
SHA = "c" * 40
WORKFLOW = Path(__file__).resolve().parents[1] / ".github/workflows/release.yml"
SOURCE = textwrap.dedent(WORKFLOW.read_text().split("python3 - <<'PY'\n", 1)[1].split("\n          PY", 1)[0])


class ReleaseSourceTests(unittest.TestCase):
    def validate(self, tag_object=None, annotated_commit=SHA, **run_overrides):
        run = dict(status="completed", conclusion="success", event="push", head_branch="main",
                   repository=dict(full_name=REPO), head_repository=dict(full_name=REPO),
                   path=".github/workflows/validate.yml", head_sha=SHA, run_attempt=2)
        run.update(run_overrides)
        project = base64.b64encode(b"<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>").decode()
        responses = {
            f"repos/{REPO}/actions/runs/123": run,
            f"repos/{REPO}/contents/src/FindCopy.App/FindCopy.App.csproj?ref={SHA}": dict(content=project),
            f"repos/{REPO}/git/matching-refs/tags/v1.0.0": [] if tag_object is None else [dict(ref="refs/tags/v1.0.0", object=tag_object)],
            f"repos/{REPO}/git/tags/{'a' * 40}": dict(object=dict(type="commit", sha=annotated_commit)),
        }

        def api(command, *, text):
            self.assertEqual(command[:2], ["gh", "api"])
            return json.dumps(responses[command[2]])

        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "output"
            env = dict(GH_REPO=REPO, RELEASE_TAG="v1.0.0", VALIDATION_RUN="123", GITHUB_OUTPUT=str(output))
            with patch.dict(os.environ, env), patch.object(subprocess, "check_output", side_effect=api), contextlib.redirect_stdout(io.StringIO()):
                exec(compile(SOURCE, str(WORKFLOW), "exec"), {})
            self.assertEqual(output.read_text(), f"sha={SHA}\n")

    def test_new_tag(self):
        self.validate()

    def test_matching_lightweight_tag(self):
        self.validate(dict(type="commit", sha=SHA))

    def test_matching_annotated_tag(self):
        self.validate(dict(type="tag", sha="a" * 40))

    def test_mismatched_lightweight_tag(self):
        with self.assertRaises(AssertionError):
            self.validate(dict(type="commit", sha="d" * 40))

    def test_mismatched_annotated_tag(self):
        with self.assertRaises(AssertionError):
            self.validate(dict(type="tag", sha="a" * 40), annotated_commit="d" * 40)

    def test_failed_validation(self):
        with self.assertRaises(AssertionError):
            self.validate(conclusion="failure")

    def test_pull_request_source(self):
        with self.assertRaises(AssertionError):
            self.validate(event="pull_request")

    def test_wrong_validation_workflow(self):
        with self.assertRaises(AssertionError):
            self.validate(path=".github/workflows/release.yml")


if __name__ == "__main__":
    unittest.main()
