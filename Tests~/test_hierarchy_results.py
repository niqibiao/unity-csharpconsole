"""Validate HierarchyRegression.cs output using an independent JSON parser.

Set CSHARPCONSOLE_HIERARCHY_RESULTS to the script's output directory.
"""
import json
import os
from pathlib import Path
import unittest

RESULTS = os.environ.get("CSHARPCONSOLE_HIERARCHY_RESULTS")


@unittest.skipUnless(RESULTS, "run HierarchyRegression.cs and set CSHARPCONSOLE_HIERARCHY_RESULTS")
class HierarchyResultsTests(unittest.TestCase):
    def read(self, name):
        return json.loads((Path(RESULTS) / (name + ".json")).read_text("utf-8-sig"))

    def nodes(self, result):
        pending = list(result["roots"]) if "roots" in result else [result["root"]]
        nodes = []
        while pending:
            node = pending.pop()
            nodes.append(node)
            pending.extend(node["children"])
        return nodes

    def test_deep_trees_are_complete_and_names_round_trip(self):
        for target in ("scene", "prefab"):
            with self.subTest(target=target):
                result = self.read(target + "-deep")
                nodes = self.nodes(result)
                self.assertEqual(32, len(nodes))
                self.assertEqual('leaf"\\中文\nline', nodes[-1]["name"])
                self.assertFalse(result["truncated"])
                self.assertEqual([], result["truncationReasons"])
                self.assertEqual(32, result["nodeCount"])

    def test_requested_depth_is_reported_separately(self):
        for target in ("scene", "prefab"):
            with self.subTest(target=target):
                result = self.read(target + "-depth")
                self.assertEqual(2, len(self.nodes(result)))
                self.assertTrue(result["truncated"])
                self.assertEqual(["requested_depth"], result["truncationReasons"])

    def test_node_budget_is_reported(self):
        for target in ("scene", "prefab"):
            with self.subTest(target=target):
                result = self.read(target + "-budget")
                self.assertEqual(5000, len(self.nodes(result)))
                self.assertEqual(5000, result["nodeCount"])
                self.assertTrue(result["truncated"])
                self.assertEqual(["node_limit"], result["truncationReasons"])

    def test_safety_depth_stays_parseable_and_reports_truncation(self):
        for target in ("scene", "prefab"):
            with self.subTest(target=target):
                result = self.read(target + "-safety-depth")
                self.assertEqual(129, len(self.nodes(result)))
                self.assertEqual(129, result["nodeCount"])
                self.assertTrue(result["truncated"])
                self.assertEqual(["depth_limit"], result["truncationReasons"])

    def test_component_names_are_qualified(self):
        for target in ("scene", "prefab"):
            with self.subTest(target=target):
                for node in self.nodes(self.read(target + "-depth")):
                    self.assertEqual(["UnityEngine.Transform"], node["components"])

    def test_inspection_emits_no_serialization_depth_warning(self):
        self.assertEqual([], self.read("warnings"))

    def test_empty_and_exact_budget_trees_are_not_marked_truncated(self):
        for name, count in (("scene-empty", 0), ("scene-exact-budget", 5000)):
            with self.subTest(name=name):
                result = self.read(name)
                self.assertEqual(count, len(self.nodes(result)))
                self.assertEqual(count, result["nodeCount"])
                self.assertFalse(result["truncated"])
                self.assertEqual([], result["truncationReasons"])

    def test_object_and_component_inspection_return_qualified_names(self):
        obj = self.read("gameobject-get")
        self.assertEqual(["UnityEngine.Transform", "UnityEngine.BoxCollider"],
                         [item["typeName"] for item in obj["components"]])
        comp = self.read("component-get")
        self.assertEqual("UnityEngine.BoxCollider", comp["typeName"])
        self.assertEqual(obj["instanceId"], comp["gameObjectInstanceId"])


if __name__ == "__main__":
    unittest.main()
