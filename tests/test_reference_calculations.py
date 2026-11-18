import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from reference_calculations import (shoe_module, storage_summary, target_people,
                                    hanger_positions, template_estimate,
                                    decompose_straight_run, estimate_candidates,
                                    split_people, room_requirements)


class CapacityTests(unittest.TestCase):
    def test_no_double_counting_two_sides(self):
        s = shoe_module(24)
        self.assertEqual(s["total_cells"], 48)
        self.assertEqual(s["capacity_people"], 24)
        self.assertEqual(s["nominal_cell_allocation_mm"], [250, 300, 200])
        self.assertIsNone(s["internal_clear_cell_mm"])

    def test_custom_and_stacked_variants(self):
        self.assertEqual([shoe_module(c)["length_mm"] for c in (12, 18, 24, 48)], [1000, 1500, 2000, 2000])
        s = shoe_module(48)
        self.assertEqual((s["total_cells"], s["physical_box_count"], s["height_mm"]), (96, 2, 1200))
        self.assertFalse(s["ordinary_crossing_surface_assumed"])
        self.assertEqual(s["footprint_m2"], shoe_module(24)["footprint_m2"])

    def test_600_storage_is_not_room_area(self):
        s = storage_summary(600)
        self.assertEqual((s["placement_groups"], s["total_cells"]), (25, 1200))
        self.assertEqual(s["cabinet_projection_area_m2"], 32.5)
        self.assertIsNone(s["room_area_m2"])
        self.assertIsNone(s["verified_effective_capacity"])

    def test_explicit_reserve_and_rounding(self):
        self.assertEqual(target_people(600, 0), 600)
        self.assertEqual(target_people(600, 0.1), 660)
        self.assertEqual(target_people(150, 0.1), 165)
        self.assertEqual(storage_summary(600, 24, 0.1)["installed_nominal_capacity"], 672)
        self.assertEqual(storage_summary(601)["placement_groups"], 26)

    def test_zero_and_bad_inputs(self):
        self.assertEqual(storage_summary(0)["placement_groups"], 0)
        for bad in (-1, 1.5, True):
            with self.assertRaises(ValueError):
                target_people(bad)
        for bad in (-0.1, float("inf"), float("nan")):
            with self.assertRaises(ValueError):
                target_people(600, bad)
        with self.assertRaises(ValueError):
            shoe_module(36)


class RoomModeTests(unittest.TestCase):
    def test_three_room_types_use_different_person_basis(self):
        r = room_requirements()["rooms"]
        self.assertEqual(r["shoe_shared"]["target_people"], 600)
        self.assertEqual(r["male_first"]["cabinet_count"], 25)
        self.assertEqual(r["female_first"]["nominal_capacity"], 300)
        self.assertEqual(r["male_first"]["cabinet_projection_area_m2"], 14.25)
        self.assertEqual(r["male_second"]["target_hooks"], 150)
        self.assertEqual(r["female_second"]["continuous_length_mm"], 15000)

    def test_odd_person_split_preserves_exact_total(self):
        self.assertEqual(split_people(601), {"male": 301, "female": 300})
        self.assertEqual(split_people(301), {"male": 151, "female": 150})

    def test_reserve_is_explicit_per_room(self):
        r = room_requirements(reserve_ratio=0.1)["rooms"]
        self.assertEqual(r["male_first"]["target_people"], 330)
        self.assertEqual(r["male_first"]["cabinet_count"], 28)
        self.assertEqual(r["male_second"]["target_hooks"], 165)
        self.assertEqual(r["male_second"]["continuous_length_mm"], 16500)

    def test_zero_person_rooms_have_no_equipment(self):
        r = room_requirements(0, 0)["rooms"]
        self.assertEqual(r["male_first"]["cabinet_count"], 0)
        self.assertEqual(r["male_second"]["continuous_length_mm"], 0)

    def test_shift_cannot_exceed_registered(self):
        with self.assertRaises(ValueError):
            room_requirements(100, 101)


class ReferenceGeometryTests(unittest.TestCase):
    def test_mixed_runs_without_stretching(self):
        r = decompose_straight_run(7500)
        self.assertEqual(r["placement_groups"], 4)
        self.assertEqual(r["capacity_people"], 90)
        self.assertEqual(r["counts"], {"S24": 3, "S18": 1})
        with self.assertRaises(ValueError):
            decompose_straight_run(1250)

    def test_documented_600_person_estimate(self):
        x = template_estimate(6, 7500, 1000)
        self.assertEqual(x["nominal_capacity"], 600)
        self.assertEqual(x["placement_groups"], 29)
        self.assertEqual(x["counts"], {"S12": 5, "S18": 6, "S24": 18})
        self.assertEqual(x["rectangle_mm"], [10800, 10900])
        self.assertEqual(x["rectangle_envelope_area_m2"], 117.72)
        self.assertEqual(x["straight_cabinet_projection_m2"], 32.5)
        self.assertEqual(x["corner_pieces"], 10)
        self.assertFalse(x["is_feasible_layout"])
        self.assertIn("end_closure_and_hygiene_topology", x["missing_validation"])

    def test_documented_612_person_estimate(self):
        x = template_estimate(5, 9000, 1500)
        self.assertEqual(x["nominal_capacity"], 612)
        self.assertEqual(x["rectangle_mm"], [12300, 11250])
        self.assertEqual(x["rectangle_envelope_area_m2"], 138.375)
        self.assertFalse(x["is_feasible_layout"])

    def test_hanger_endpoints_are_explicit_and_not_doubled(self):
        self.assertEqual(hanger_positions(1000)["positions_mm"], list(range(50, 1000, 100)))
        self.assertEqual(hanger_positions(15000)["hooks"], 150)
        self.assertEqual(hanger_positions(16500)["hooks"], 165)
        self.assertEqual(hanger_positions(50)["hooks"], 0)

    def test_zero_length_hanger_cannot_have_hook_even_without_end_margin(self):
        self.assertEqual(hanger_positions(0, end_margin_mm=0)["hooks"], 0)

    def test_enumerator_never_claims_verified_layout(self):
        items = estimate_candidates(600, limit=3)
        self.assertEqual(len(items), 3)
        self.assertTrue(all(x["nominal_capacity"] >= 600 for x in items))
        self.assertTrue(all(x["is_feasible_layout"] is False for x in items))
        self.assertEqual(len({x["template_id"] for x in items}), 3)


if __name__ == "__main__":
    unittest.main()
