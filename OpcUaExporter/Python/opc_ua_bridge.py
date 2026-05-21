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


def browse_recursive(node: Node, result: list, depth: int = 0, max_depth: int = 8):
    """Recursively walk the address space and collect Variable nodes."""
    if depth > max_depth:
        return
    try:
        children = node.get_children()
    except Exception:
        return

    for child in children:
        try:
            nc = child.get_node_class()
            browse_name = child.get_browse_name()
            display_name = child.get_display_name().Text
            node_id = child.nodeid.to_string()

            entry = {
                "nodeId": node_id,
                "browseName": f"{browse_name.NamespaceIndex}:{browse_name.Name}",
                "displayName": display_name,
                "nodeClass": node_class_name(nc),
                "children": []
            }

            if nc == ua.NodeClass.Variable:
                try:
                    dv = child.get_data_value()
                    entry["dataType"] = variant_type_name(dv.Value.VariantType)
                    entry["value"] = safe_value(dv.Value.Value)
                    entry["quality"] = str(dv.StatusCode)
                except Exception:
                    entry["dataType"] = "Unknown"
                    entry["value"] = None
                    entry["quality"] = "Error"
                result.append(entry)
            elif nc == ua.NodeClass.Object:
                sub_result = []
                browse_recursive(child, sub_result, depth + 1, max_depth)
                entry["children"] = sub_result
                result.append(entry)
        except Exception:
            continue


def connect(endpoint_url: str) -> Client:
    client = Client(endpoint_url)
    client.connect()
    return client


# ---------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------

def cmd_browse(endpoint_url: str):
    client = connect(endpoint_url)
    try:
        root = client.get_objects_node()
        result = []
        browse_recursive(root, result)
        print(json.dumps({"ok": True, "tags": result}))
    finally:
        client.disconnect()


def cmd_read(endpoint_url: str, node_ids: list):
    client = connect(endpoint_url)
    try:
        rows = []
        for nid in node_ids:
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
        print(json.dumps({"ok": True, "rows": rows}))
    finally:
        client.disconnect()


def cmd_export(endpoint_url: str, node_ids: list, output_path: str):
    client = connect(endpoint_url)
    try:
        rows = []
        for nid in node_ids:
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
            with open(output_path, "w", newline="", encoding="utf-8") as f:
                if rows:
                    writer = csv.DictWriter(f, fieldnames=rows[0].keys())
                    writer.writeheader()
                    writer.writerows(rows)
        else:
            with open(output_path, "w", encoding="utf-8") as f:
                json.dump(rows, f, indent=2)

        print(json.dumps({"ok": True, "exported": len(rows), "path": output_path}))
    finally:
        client.disconnect()


# ---------------------------------------------------------------------------
# Entry-point
# ---------------------------------------------------------------------------

def main():
    if len(sys.argv) < 2:
        print(json.dumps({"error": "No command specified. Use: browse | read | export"}),
              file=sys.stderr)
        sys.exit(1)

    command = sys.argv[1].lower()

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
