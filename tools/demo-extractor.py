#!/usr/bin/env python3
"""Extract the small CS2 demo subset that StrafeLab needs.

The script intentionally writes JSON Lines to stdout and diagnostics to stderr.
It uses demoparser2's public Python API, so StrafeLab can keep the demo parser
optional and local. No game process, memory, or network service is touched.
"""

from __future__ import annotations

import argparse
import bz2
import statistics
import json
import math
import sys
from collections.abc import Iterable, Mapping
from pathlib import Path
from typing import Any


DEFAULT_TICKRATE = 64.0
EVENTS = ("weapon_fire", "round_start", "round_freeze_end", "player_spawn")


def emit(value: Mapping[str, Any]) -> None:
    print(json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False))


def clean(value: Any) -> Any:
    """Convert pandas/numpy scalars and NaN values into JSON-safe values."""

    if value is None:
        return None
    try:
        if bool(value != value):  # NaN, including numpy floating scalars.
            return None
    except Exception:
        pass
    if hasattr(value, "item"):
        try:
            value = value.item()
        except Exception:
            pass
    if isinstance(value, (str, int, float, bool)):
        if isinstance(value, float) and not math.isfinite(value):
            return None
        return value
    return str(value)


def row_dict(row: Any) -> dict[str, Any]:
    if isinstance(row, Mapping):
        return {str(key): clean(value) for key, value in row.items()}
    if hasattr(row, "to_dict"):
        return {str(key): clean(value) for key, value in row.to_dict().items()}
    return {}


def get_value(row: Mapping[str, Any], *names: str) -> Any:
    direct = {str(key): value for key, value in row.items()}
    lowered = {str(key).lower(): value for key, value in row.items()}
    for name in names:
        if name in direct:
            return direct[name]
        if name.lower() in lowered:
            return lowered[name.lower()]
    return None


def get_tick(row: Mapping[str, Any]) -> int | None:
    value = get_value(row, "tick", "net_tick", "game_tick")
    try:
        return int(value) if value is not None else None
    except (TypeError, ValueError):
        return None


def get_steam_id(row: Mapping[str, Any]) -> str | None:
    # demoparser2 names the event-side field according to the event userid,
    # e.g. user_steamid, while tick rows use steamid.
    for key, value in row.items():
        normalized = str(key).lower().replace("_", "")
        if "steamid" not in normalized:
            continue
        try:
            if value is not None and not (isinstance(value, float) and math.isnan(value)):
                return str(int(value))
        except (TypeError, ValueError):
            if value is not None:
                return str(value)
    return None


def as_int(row, *names):
    value = get_value(row, *names)
    return int(value) if value is not None else None


def as_float(row: Mapping[str, Any], *names: str) -> float | None:
    value = get_value(row, *names)
    try:
        return float(value) if value is not None else None
    except (TypeError, ValueError):
        return None


def parse_event_frame(parser: Any, event_name: str) -> Iterable[Any]:
    """Return an event frame, tolerating event-specific parser differences."""

    try:
        # Request the player steam id when supported. The no-argument fallback
        # still exposes the standard event columns on older demoparser2 builds.
        return parser.parse_event(event_name, player=["player_steamid"], other=["total_rounds_played"])
    except Exception:
        try:
            return parser.parse_event(event_name)
        except Exception as exc:
            print(f"event {event_name}: {exc}", file=sys.stderr)
            return []


def event_observations(parser: Any, event_name: str, steam_id: str | None, tickrate: float) -> int:
    count = 0
    frame = parse_event_frame(parser, event_name)
    try:
        rows = frame.to_dict("records")
    except Exception:
        rows = [row_dict(row) for row in frame]

    for raw in rows:
        row = row_dict(raw)
        tick = get_tick(row)
        if tick is None:
            continue
        event_steam_id = get_steam_id(row)
        if event_name in ("weapon_fire", "player_spawn") and steam_id and event_steam_id != steam_id:
            continue
        emit(
            {
                "tick": tick,
                "time_seconds": tick / tickrate,
                "kind": "game_event",
                "event_name": event_name,
                "steam_id": event_steam_id,
                "weapon": get_value(row, "weapon", "weapon_name", "item"),
                "round": as_int(row,"total_rounds_played"),
            }
        )
        count += 1
    return count


def read_ticks(parser: Any, steam_id: str | None):
    wanted = [
        "X",
        "Y",
        "Z",
        "velocity_X",
        "velocity_Y",
        "velocity_Z",
        "player_steamid",
        "entity_id",
        "is_alive",
        "active_weapon_name",
        "game_time", "is_airborne", "is_scoped", "duck_amount", "yaw", "move_type",
        "total_rounds_played", "velo_modifier", "is_walking", "max_speed",
    ]
    kwargs: dict[str, Any] = {}
    if steam_id:
        kwargs["players"] = [int(steam_id)]

    try:
        frame = parser.parse_ticks(wanted, **kwargs)
    except Exception as first_error:
        # Some older builds expose the raw field names but not all convenience
        # aliases. Coordinates and velocity aliases are the useful minimum.
        fallback = ["X", "Y", "Z", "velocity_X", "velocity_Y", "velocity_Z"]
        try:
            frame = parser.parse_ticks(fallback, **kwargs)
        except Exception as second_error:
            raise RuntimeError(f"parse_ticks failed: {first_error}; fallback failed: {second_error}") from second_error

    return frame


def prepare_velocities(frame):
    """Timestamp velocity at the END of its actual position interval.

    demoparser2 0.42 velocity aliases read previous position history; do not
    silently label that one-tick-old value as speed at the current tick.
    """
    import numpy as np
    frame = frame.sort_values(["steamid", "tick"]).copy()
    if "game_time" not in frame:
        raise RuntimeError("game_time unavailable; cannot verify Demo clock")
    groups = frame.groupby("steamid", sort=False)
    dt = groups["game_time"].diff()
    ticks = groups["tick"].diff()
    ratios = (ticks / dt).replace([np.inf, -np.inf], np.nan)
    valid = ratios[(dt > 0) & (ticks > 0)].dropna()
    if len(valid) < 32:
        raise RuntimeError("Not enough samples to verify Demo clock/player identity")
    rate = float(valid.median())
    if not 30 <= rate <= 256 or float((abs(valid-rate) < rate*.002).mean()) < .98:
        raise RuntimeError("Demo clock is irregular; synchronization rejected")
    for axis in ("X", "Y", "Z"):
        delta = groups[axis].diff()
        frame["velocity_"+axis] = (delta/dt).where((ticks == 1) & (dt > 0) & (dt < .04))
    return frame, rate


def tick_observations(frame: Any, steam_id: str | None, tickrate: float, sample_every: int) -> tuple[int, int]:
    rows = frame.to_dict("records")

    emitted = 0
    max_tick = 0
    for raw in rows:
        row = row_dict(raw)
        tick = get_tick(row)
        if tick is None:
            continue
        max_tick = max(max_tick, tick)
        if sample_every > 1 and tick % sample_every != 0:
            continue
        row_steam_id = get_steam_id(row)
        if steam_id and row_steam_id != steam_id:
            continue
        emit(
            {
                "tick": tick,
                "time_seconds": tick / tickrate,
                "kind": "player_sample",
                "event_name": None,
                "steam_id": row_steam_id,
                "entity_index": as_int(row, "entity_id", "entity_index"),
                "x": as_float(row, "X", "x"),
                "y": as_float(row, "Y", "y"),
                "z": as_float(row, "Z", "z"),
                "velocity_x": as_float(row, "velocity_X", "velocity_x"),
                "velocity_y": as_float(row, "velocity_Y", "velocity_y"),
                "velocity_z": as_float(row, "velocity_Z", "velocity_z"),
                "weapon": get_value(row, "active_weapon_name", "weapon"),
                "is_alive": get_value(row,"is_alive"),
                "on_ground": None if get_value(row,"is_airborne") is None else not bool(get_value(row,"is_airborne")),
                "is_scoped": get_value(row,"is_scoped"),
                "duck_amount": as_float(row,"duck_amount"),
                "yaw": as_float(row,"yaw"),
                "round": as_int(row,"total_rounds_played"),
                "move_type": as_int(row,"move_type"),
                "velocity_modifier": as_float(row,"velo_modifier"),
                "is_walking": get_value(row,"is_walking"),
                "max_speed": as_float(row,"max_speed"),
            }
        )
        emitted += 1
    return emitted, max_tick


def main() -> int:
    argument_parser = argparse.ArgumentParser(description=__doc__)
    argument_parser.add_argument("--input", required=True, type=Path)
    argument_parser.add_argument("--steamid")
    argument_parser.add_argument("--tickrate", type=float, help="Optional assertion against measured tick rate")
    argument_parser.add_argument("--unpack-bz2", action="store_true")
    argument_parser.add_argument("--output", type=Path)
    argument_parser.add_argument("--sample-every", type=int, default=1)
    args = argument_parser.parse_args()

    if not args.input.is_file():
        print(f"demo not found: {args.input}", file=sys.stderr)
        return 2
    if args.unpack_bz2:
        if args.output is None:
            return 2
        temporary = args.output.with_suffix(".tmp")
        try:
            total = 0
            with bz2.open(args.input, "rb") as source, temporary.open("wb") as target:
                while block := source.read(1024*1024):
                    total += len(block)
                    if total > 2_000_000_000:
                        raise RuntimeError("Decompressed Demo exceeds 2 GB")
                    target.write(block)
            with temporary.open("rb") as check:
                if check.read(8) != b"PBDEMS2\0":
                    raise RuntimeError("Not a Source 2 Demo")
            temporary.replace(args.output)
            return 0
        except Exception as exc:
            print(str(exc), file=sys.stderr)
            return 4
        finally:
            temporary.unlink(missing_ok=True)

    try:
        from demoparser2 import DemoParser
    except Exception as exc:
        print("demoparser2 is not installed; install it with: python -m pip install demoparser2", file=sys.stderr)
        print(str(exc), file=sys.stderr)
        return 3

    try:
        parser = DemoParser(str(args.input))
        header = parser.parse_header()
        map_name = header.get("map_name") if isinstance(header, Mapping) else None
        raw_frame = read_ticks(parser, args.steamid)
        if len(raw_frame) == 0:
            emit({"kind":"meta","map":map_name,"tick":0,"time_seconds":0})
            print("Requested player absent; no alignment possible", file=sys.stderr)
            return 0
        frame, tickrate = prepare_velocities(raw_frame)
        if args.tickrate and abs(args.tickrate-tickrate) > .01:
            raise RuntimeError("Requested tick rate differs from measured Demo clock")
        emit(
            {
                "tick": 0,
                "time_seconds": 0.0,
                "kind": "meta",
                "event_name": None,
                "map": map_name,
                "tick_rate": tickrate,
            }
        )

        available_events = set(parser.list_game_events())
        event_count = 0
        for event_name in EVENTS:
            if event_name in available_events:
                event_count += event_observations(parser, event_name, args.steamid, tickrate)

        sample_count, max_tick = tick_observations(
            frame,
            args.steamid,
            tickrate,
            max(1, args.sample_every),
        )
        print(
            f"parsed {event_count} events and {sample_count} player samples from {args.input.name}",
            file=sys.stderr,
        )
        # A second metadata record carries the best tick count without making
        # the C# consumer depend on a separate side-channel file.
        emit(
            {
                "tick": max_tick,
                "time_seconds": max_tick / tickrate,
                "kind": "meta_end",
                "event_name": None,
                "map": map_name,
                "tick_rate": tickrate,
            }
        )
        return 0
    except Exception as exc:
        print(f"demo parse failed: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 4


if __name__ == "__main__":
    raise SystemExit(main())
