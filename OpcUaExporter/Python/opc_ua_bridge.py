"""
opc_ua_bridge.py
OPC UA bridge script called by the .NET host process.
Uses the 'opcua' (python-opcua) free library.

Usage:
    python opc_ua_bridge.py <command> [args_json]

Commands:
    browse   <endpoint_url>                         -> JSON list of tags
    read     <endpoint_url> <node_ids_json>         -> JSON list of {nodeId, value, type, quality, timestamp}
    export   <endpoint_url> <node_ids_json> <path>  -> writes CSV/JSON to path, returns status JSON
"""

import sys
import json
import traceback
import csv
import os
from datetime import datetime, timezone

# ---------------------------------------------------------------------------
# opcua import – fails gracefully so the host can surface a clear error
# ---------------------------------------------------------------------------
try:
    from opcua import Client, ua
    from opcua.common.node import Node
except ImportError as e:
    print(json.dumps({"error": f"opcua library not found: {e}. "
                               "Run: python -m pip install opcua --target <runtime_dir>\\lib"}),
          file=sys.stderr)
    sys.exit(2)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def log(message: str):
    ts = datetime.now(timezone.utc).isoformat()
    print(f"[bridge][{ts}] {message}", file=sys.stderr, flush=True)

def node_class_name(nc):
    try:
        return nc.name
    except Exception:
        return str(nc)


def variant_type_name(vt):
    try:
        return vt.name
    except Exception:
        return str(vt)


def safe_value(val):
    """Convert a DV value to a JSON-serialisable Python object."""
    if val is None:
        return None
    if isinstance(val, (int, float, bool, str)):
        return val
    if isinstance(val, bytes):
        return val.hex()
    if isinstance(val, list):
        return [safe_value(v) for v in val]
    return str(val)


def sort_tree_by_nodeid(entries: list):
    for entry in entries:
        children = entry.get("children")
        if isinstance(children, list) and children:
            sort_tree_by_nodeid(children)

    entries.sort(key=lambda e: str(e.get("nodeId", "")).lower())


def env_flag(name: str, default: bool) -> bool:
    value = os.getenv(name)
    if value is None:
        return default
    return value.strip().lower() in ("1", "true", "yes", "on")


def env_int(name: str, default: int) -> int:
    value = os.getenv(name)
    if value is None:
        return default
    try:
        return int(value)
    except Exception:
        return default


def browse_recursive(
    node: Node,
    result: list,
    progress: dict,
    depth: int = 0,
    max_depth: int = 8,
    include_data_type: bool = False
):
    """Recursively walk the address space and collect configuration metadata."""
    if depth > max_depth:
        return
    try:
        children = node.get_children_descriptions()
    except Exception:
        return

    for child in children:
        try:
            progress["visited"] += 1
            if progress["visited"] % 200 == 0:
                log(f"browse progress: visited={progress['visited']} depth={depth}")

            nc = child.NodeClass
            browse_name = child.BrowseName
            display_name = child.DisplayName.Text if child.DisplayName else ""
            node_id = child.NodeId.to_string()

            entry = {
                "nodeId": node_id,
                "browseName": f"{browse_name.NamespaceIndex}:{browse_name.Name}",
                "displayName": display_name,
                "nodeClass": node_class_name(nc),
                "children": []
            }

            if nc == ua.NodeClass.Variable:
                if include_data_type:
                    try:
                        data_node = node.server.get_node(node_id)
                        entry["dataType"] = variant_type_name(data_node.get_data_type_as_variant_type())
                    except Exception:
                        entry["dataType"] = "Unknown"
                else:
                    entry["dataType"] = "Unknown"
                result.append(entry)
            elif nc == ua.NodeClass.Object:
                sub_result = []
                child_node = node.server.get_node(node_id)
                browse_recursive(child_node, sub_result, progress, depth + 1, max_depth, include_data_type)
                entry["children"] = sub_result
                result.append(entry)
        except Exception:
            continue


def connect(endpoint_url: str) -> Client:
    log(f"connect: creating client for endpoint={endpoint_url}")
    client = Client(endpoint_url)
    log("connect: connecting...")
    client.connect()
    log("connect: connected")
    return client


# ---------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------

def cmd_browse(endpoint_url: str):
    log("browse: start")
    client = connect(endpoint_url)
    try:
        log("browse: retrieving Objects node")
        root = client.get_objects_node()
        result = []
        progress = {"visited": 0}
        max_depth = env_int("OPCUA_BROWSE_MAX_DEPTH", 8)
        include_data_type = env_flag("OPCUA_BROWSE_INCLUDE_DATATYPE", False)
        log(f"browse: settings max_depth={max_depth} include_data_type={include_data_type}")
        log("browse: walking address space")
        browse_recursive(root, result, progress, max_depth=max_depth, include_data_type=include_data_type)
        sort_tree_by_nodeid(result)
        log(f"browse: completed visited={progress['visited']} entries={len(result)}")
        print(json.dumps({"ok": True, "tags": result}))
    finally:
        log("browse: disconnecting")
        client.disconnect()
        log("browse: done")


def cmd_read(endpoint_url: str, node_ids: list):
    log(f"read: start count={len(node_ids)}")
    client = connect(endpoint_url)
    try:
        rows = []
        for i, nid in enumerate(node_ids, start=1):
            if i == 1 or i % 25 == 0 or i == len(node_ids):
                log(f"read progress: {i}/{len(node_ids)}")
            try:
                node = client.get_node(nid)
                dv = node.get_data_value()
                display = node.get_display_name().Text
                rows.append({
                    "nodeId": nid,
                    "displayName": display,
                    "value": safe_value(dv.Value.Value),
                    "dataType": variant_type_name(dv.Value.VariantType),
                    "quality": str(dv.StatusCode),
                    "timestamp": dv.SourceTimestamp.isoformat() if dv.SourceTimestamp else None
                })
            except Exception as ex:
                rows.append({"nodeId": nid, "error": str(ex)})
        log(f"read: completed rows={len(rows)}")
        print(json.dumps({"ok": True, "rows": rows}))
    finally:
        log("read: disconnecting")
        client.disconnect()
        log("read: done")


def cmd_export(endpoint_url: str, node_ids: list, output_path: str):
    log(f"export: start count={len(node_ids)} path={output_path}")
    client = connect(endpoint_url)
    try:
        rows = []
        for i, nid in enumerate(node_ids, start=1):
            if i == 1 or i % 25 == 0 or i == len(node_ids):
                log(f"export progress: {i}/{len(node_ids)}")
            try:
                node = client.get_node(nid)
                dv = node.get_data_value()
                rows.append({
                    "nodeId": nid,
                    "displayName": node.get_display_name().Text,
                    "value": safe_value(dv.Value.Value),
                    "dataType": variant_type_name(dv.Value.VariantType),
                    "quality": str(dv.StatusCode),
                    "timestamp": dv.SourceTimestamp.isoformat() if dv.SourceTimestamp else None,
                    "exportedAt": datetime.now(timezone.utc).isoformat()
                })
            except Exception as ex:
                rows.append({
                    "nodeId": nid, "displayName": "", "value": None,
                    "dataType": "Error", "quality": str(ex), "timestamp": None,
                    "exportedAt": datetime.now(timezone.utc).isoformat()
                })

        ext = os.path.splitext(output_path)[1].lower()
        if ext == ".csv":
            log("export: writing CSV")
            with open(output_path, "w", newline="", encoding="utf-8") as f:
                if rows:
                    writer = csv.DictWriter(f, fieldnames=rows[0].keys())
                    writer.writeheader()
                    writer.writerows(rows)
        else:
            log("export: writing JSON")
            with open(output_path, "w", encoding="utf-8") as f:
                json.dump(rows, f, indent=2)

        log(f"export: completed rows={len(rows)}")
        print(json.dumps({"ok": True, "exported": len(rows), "path": output_path}))
    finally:
        log("export: disconnecting")
        client.disconnect()
        log("export: done")


# ---------------------------------------------------------------------------
# Entry-point
# ---------------------------------------------------------------------------

def main():
    if len(sys.argv) < 2:
        print(json.dumps({"error": "No command specified. Use: browse | read | export"}),
              file=sys.stderr)
        sys.exit(1)

    command = sys.argv[1].lower()
    log(f"main: command={command}")

    try:
        if command == "browse":
            if len(sys.argv) < 3:
                raise ValueError("browse requires <endpoint_url>")
            cmd_browse(sys.argv[2])

        elif command == "read":
            if len(sys.argv) < 4:
                raise ValueError("read requires <endpoint_url> <node_ids_json>")
            node_ids = json.loads(sys.argv[3])
            cmd_read(sys.argv[2], node_ids)

        elif command == "export":
            if len(sys.argv) < 5:
                raise ValueError("export requires <endpoint_url> <node_ids_json> <output_path>")
            node_ids = json.loads(sys.argv[3])
            cmd_export(sys.argv[2], node_ids, sys.argv[4])

        else:
            raise ValueError(f"Unknown command: {command}")

    except Exception as ex:
        print(json.dumps({"error": str(ex), "trace": traceback.format_exc()}),
              file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
