#!/usr/bin/env python3
"""Reference arithmetic only. No CAD block generation or validated layout solver.

All lengths are millimetres. Estimates retain unverified conditions explicitly.
Run: python tools/reference_calculations.py --people 600 --reserve 0
"""
from __future__ import annotations

import argparse
from collections import Counter
from decimal import Decimal, ROUND_CEILING, ROUND_HALF_UP
import json
import math

SHOE_DEPTH_MM = 650
SHOE_DUCT_MM = 50
CORNER_RADIUS_MM = 650


def _integer(value: int, name: str, minimum: int = 0) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < minimum:
        raise ValueError(f"{name} must be an integer >= {minimum}")
    return value


def target_people(registered: int, reserve_ratio: float = 0.0) -> int:
    _integer(registered, "registered")
    ratio = Decimal(str(reserve_ratio))
    if not ratio.is_finite() or ratio < 0:
        raise ValueError("reserve_ratio must be finite and non-negative")
    return int((Decimal(registered) * (1 + ratio)).to_integral_value(rounding=ROUND_CEILING))


def shoe_module(capacity: int) -> dict:
    """250/300/200 are nominal allocated pitches, never net internal clear sizes."""
    _integer(capacity, "capacity", 1)
    if capacity not in (12, 18, 24, 48):
        raise ValueError("Supported user variants: 12, 18, 24, 48 people")
    stacks = 2 if capacity == 48 else 1
    per_box_people = capacity // stacks
    columns = per_box_people // 3
    return {
        "sku": f"S{capacity}", "capacity_people": capacity,
        "columns_per_side_per_box": columns, "vertical_cells_per_box": 3,
        "stack_boxes": stacks, "physical_box_count": stacks,
        "cells_per_person": 2, "total_cells": 2 * capacity,
        "length_mm": columns * 250, "depth_mm": SHOE_DEPTH_MM, "height_mm": 600 * stacks,
        "nominal_cell_allocation_mm": [250, 300, 200],
        "central_ventilation_duct_mm": SHOE_DUCT_MM,
        "internal_clear_cell_mm": None,
        "footprint_m2": columns * 250 * SHOE_DEPTH_MM / 1_000_000,
        "ordinary_crossing_surface_assumed": stacks == 1,
        "crossing_verified": False,
        "dimension_status": "source_and_latest_user" if capacity == 24 else "derived_custom_module",
    }


def storage_summary(people: int, capacity: int = 24, reserve_ratio: float = 0.0) -> dict:
    target = target_people(people, reserve_ratio)
    module = shoe_module(capacity)
    count = (target + capacity - 1) // capacity
    installed = count * capacity
    return {
        "registered_people": people, "reserve_ratio": reserve_ratio, "target_people": target,
        "placement_groups": count, "physical_boxes": count * module["stack_boxes"],
        "installed_nominal_capacity": installed, "total_cells": installed * 2,
        "nominal_surplus": installed - target,
        "cabinet_projection_area_m2": round(count * module["footprint_m2"], 6),
        "verified_effective_capacity": None, "room_area_m2": None,
        "status": "storage_arithmetic_only",
    }


def decompose_straight_run(length_mm: int) -> dict:
    """Use exact 1000/1500/2000 lengths, fewest physical boxes; no stretching."""
    _integer(length_mm, "length_mm", 1)
    if length_mm % 250:
        raise ValueError("run must respect the 250 mm column pitch")
    sizes = ((2000, 24), (1500, 18), (1000, 12))
    best: dict[int, tuple[int, ...]] = {0: ()}
    for end in range(250, length_mm + 1, 250):
        for length, cap in sizes:
            if end - length not in best:
                continue
            candidate = best[end - length] + (cap,)
            previous = best.get(end)
            if previous is None or (len(candidate), tuple(sorted(candidate, reverse=True))) < (
                len(previous), tuple(sorted(previous, reverse=True))
            ):
                best[end] = candidate
    if length_mm not in best:
        raise ValueError("run cannot be formed from the allowed whole cabinets")
    counts = Counter(best[length_mm])
    return {
        "length_mm": length_mm,
        "counts": {f"S{k}": counts[k] for k in sorted(counts, reverse=True)},
        "placement_groups": sum(counts.values()),
        "capacity_people": sum(k * n for k, n in counts.items()),
    }


def hanger_positions(length_mm: int, pitch_mm: int = 100, end_margin_mm: int = 50) -> dict:
    """One independently confirmed hook line. 50 mm margins are a configurable assumption."""
    _integer(length_mm, "length_mm")
    _integer(pitch_mm, "pitch_mm", 1)
    _integer(end_margin_mm, "end_margin_mm")
    if length_mm == 0 or length_mm < 2 * end_margin_mm:
        positions = []
    else:
        positions = list(range(end_margin_mm, length_mm - end_margin_mm + 1, pitch_mm))
    return {
        "positions_mm": positions, "hooks": len(positions), "hook_lines_counted": 1,
        "pitch_mm": pitch_mm, "end_margin_mm": end_margin_mm,
        "end_margin_status": "configurable_assumption_not_in_source_table",
        "garment_operation_capacity": None,
    }


def split_people(total: int, male_ratio: float = 0.5) -> dict:
    """Deterministic split preserving totals; .5 rounding ties assigned to male."""
    _integer(total, "total")
    ratio = Decimal(str(male_ratio))
    if not ratio.is_finite() or not 0 <= ratio <= 1:
        raise ValueError("male_ratio must be between zero and one")
    male = int((Decimal(total) * ratio).to_integral_value(rounding=ROUND_HALF_UP))
    return {"male": male, "female": total - male}


def room_requirements(registered: int = 600, per_shift: int = 300,
                      male_ratio: float = 0.5, reserve_ratio: float = 0.0) -> dict:
    """Three required room types, separate storage and shift-time hanging counts."""
    _integer(registered, "registered")
    _integer(per_shift, "per_shift")
    if per_shift > registered:
        raise ValueError("per_shift cannot exceed registered in this roster model")
    roster, shift = split_people(registered, male_ratio), split_people(per_shift, male_ratio)
    rooms = {"shoe_shared": storage_summary(registered, 24, reserve_ratio)}
    for sex in ("male", "female"):
        target = target_people(roster[sex], reserve_ratio)
        boxes = (target + 11) // 12
        rooms[f"{sex}_first"] = {
            "room_mode": "first_change", "allocation_basis": "registered_people",
            "assigned_roster_people": roster[sex], "target_people": target,
            "equipment_id": "LOCKER12", "cabinet_count": boxes,
            "nominal_capacity": boxes * 12, "nominal_surplus": boxes * 12 - target,
            "cabinet_projection_area_m2": round(boxes * 1140 * 500 / 1_000_000, 6),
            "verified_room_area_m2": None, "status": "arithmetic_only",
        }
        hooks = target_people(shift[sex], reserve_ratio)
        length = 0 if hooks == 0 else (hooks - 1) * 100 + 50 + 50
        rooms[f"{sex}_second"] = {
            "room_mode": "second_change", "allocation_basis": "peak_shift_people",
            "assigned_shift_people": shift[sex], "target_hooks": hooks,
            "equipment_id": "HANGER_CUSTOM", "continuous_length_mm": length,
            "pitch_mm": 100, "end_margins_mm": [50, 50],
            "end_margins_status": "assumption_not_source_dimension",
            "verified_room_area_m2": None, "status": "arithmetic_only",
        }
    return {"registered_split": roster, "shift_split": shift,
            "reserve_application": "ceil_per_room_after_sex_split",
            "rooms": rooms, "all_three_room_modes_required_in_first_release": True}


def template_estimate(rows: int, run_mm: int, connector_mm: int,
                      boundary_mm: int = 1000) -> dict:
    """Canonical OUTSIDE tangent quarter-sector estimate, not source-CAD reconstruction.

    d=R=650, including the 50 mm central duct. Every connector lies outside straight-row ends; its length fills
    the clear row gap. Two quarter sectors per U turn. End seals, doors,
    fixtures, actual corner shape and face access are deliberately unverified.
    """
    _integer(rows, "rows", 3)
    _integer(boundary_mm, "boundary_mm", 0)
    if connector_mm not in (1000, 1500, 2000):
        raise ValueError("connector must be S12, S18, or S24")
    run = decompose_straight_run(run_mm)
    connector = decompose_straight_run(connector_mm)
    counts = Counter({k: v * rows for k, v in run["counts"].items()})
    for k, v in connector["counts"].items():
        counts[k] += v * (rows - 1)
    r, d = CORNER_RADIUS_MM, SHOE_DEPTH_MM
    turns = rows - 1
    corners = turns * 2
    width = run_mm + 2 * r + 2 * boundary_mm
    height = rows * d + turns * connector_mm + 2 * boundary_mm
    capacity = rows * run["capacity_people"] + turns * connector["capacity_people"]
    straight_area = (rows * run_mm * d + turns * connector_mm * d) / 1_000_000
    corner_area = corners * math.pi * r ** 2 / 4 / 1_000_000
    return {
        "template_id": f"outside_tangent_sector_R650_r{rows}_L{run_mm}_c{connector_mm}",
        "rows": rows, "run_mm": run_mm, "connector_mm": connector_mm,
        "clear_row_gap_mm_in_this_template": connector_mm,
        "boundary_clearance_mm": boundary_mm,
        "counts": dict(sorted(counts.items())), "placement_groups": sum(counts.values()),
        "physical_boxes": sum(counts.values()), "corner_pieces": corners,
        "nominal_capacity": capacity, "total_cells": capacity * 2,
        "rectangle_mm": [width, height],
        "rectangle_envelope_area_m2": round(width * height / 1_000_000, 6),
        "straight_cabinet_projection_m2": round(straight_area, 6),
        "assumed_corner_projection_m2": round(corner_area, 6),
        "is_feasible_layout": False, "validation_state": "estimate_only",
        "missing_validation": [
            "actual_source_block_and_corner_geometry", "end_closure_and_hygiene_topology",
            "entry_exit_door_conflicts", "both_fronts_and_cabinet_doors",
            "simultaneous_shoe_changing", "ancillary_equipment_and_actual_boundary",
        ],
        "note": "柜阵包络预估，不是已通过使用验证的房间面积；未封闭S阵可绕端。",
    }


def estimate_candidates(people: int, reserve_ratio: float = 0.0, limit: int = 6) -> list[dict]:
    """Bounded template estimates only; effective topology and actual CAD remain absent."""
    target = target_people(people, reserve_ratio)
    _integer(limit, "limit", 1)
    if not target:
        return []
    if target > 10_000:
        raise ValueError("reference enumeration is bounded to 10000; production solver must be cancellable")
    candidates = []
    max_run = max(2000, min(50_000, ((target + 35) // 36) * 1000))
    for rows in range(3, 13):
        for connector in (1000, 1500, 2000):
            for run in range(1000, max_run + 1, 500):
                item = template_estimate(rows, run, connector)
                if item["nominal_capacity"] < target:
                    continue
                item["target_people"] = target
                item["nominal_surplus"] = item["nominal_capacity"] - target
                candidates.append(item)
    candidates.sort(key=lambda x: (x["rectangle_envelope_area_m2"], x["nominal_surplus"], x["placement_groups"]))
    return candidates[:limit]


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--people", type=int, default=600)
    parser.add_argument("--reserve", type=float, default=0)
    args = parser.parse_args()
    result = {
        "scope": "handoff_reference_arithmetic_not_a_cad_plugin",
        "module_table": [shoe_module(n) for n in (12, 18, 24, 48)],
        "uniform_S24": storage_summary(args.people, 24, args.reserve),
        "stacked_only_arithmetic_not_layout": storage_summary(args.people, 48, args.reserve),
        "unverified_envelope_estimates": estimate_candidates(args.people, args.reserve),
        "explicit_600_person_example": template_estimate(6, 7500, 1000),
        "three_room_defaults": room_requirements(args.people, min(300, args.people),
                                                 reserve_ratio=args.reserve),
    }
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
