# Troubleshooting

Operational quirks and error codes you'll run into when driving the tools
for real, beyond what the [tools reference](tools-reference.md) covers.

## Tool call sequence

```
beckhoff_connect → beckhoff_list_symbols → beckhoff_read_variables
```

Always connect first. Discovery and read are separate calls — find paths
first, then read values.

## Symbol table basics

The flat symbol list contains only **top-level instances and flagged I/O
members**. Nested FB members are **not** in the flat list. They are
readable by explicit path but not findable by flat search.

Use `recurse=true` with `parent_path` to walk the type tree via
`SubSymbols` (lazy, local type table — no extra ADS round-trip per level):

```
beckhoff_list_symbols parent_path="MyPrg.myFB" recurse=true pattern="MemberName$" max_depth=8
```

`parent_path` is required with `recurse=true` — full-tree recursion of all
root symbols is too expensive.

## PROGRAM symbols are not directly resolvable

PROGRAM-typed symbols cannot be resolved as a root node. The walker falls
back to deriving child instances from the flat table by first-segment
prefix. This handles array-element instances automatically.

## Cold-start symbol load

After a fresh connect the symbol table is empty on the **first tool call**.
Warmup fires in the background at connect time; the second call reliably
returns the full table. If `beckhoff_list_symbols` returns 0 symbols, call
it once more.

## Timeout

`timeout_seconds` sets both the CancellationToken **and**
`AdsConnection.Timeout`. Default is 30 s (raised from the ADS default of
5 s). The 5 s ADS default fires as `ClientSyncTimeOut` before the
CancellationToken expires — always pass `timeout_seconds` explicitly on
slow or VPN links.

## Batch read resiliency

A single read timeout calls `InvalidateConnection()`. The connection is
re-acquired per read inside the batch — one failure does not poison the
rest. Pass `timeout_seconds` per `beckhoff_read_variables` call.

## Large / complex symbols

`FunctionBlock` and `Program` symbols with `size > max_bytes` (default
4096) are returned as raw base64 bytes (IndexGroup/Offset/Size read), not
typed values. This avoids an uncatchable native StackOverflow from deep
typed materialisation.

## Pointer / Interface / Reference members

These are **leaves by default** (`deref_pointers=false`) — the walker does
not recurse into them. This is the cycle guard: PLC programs often have
cross-referencing pointers. Use `deref_pointers=true` only when explicitly
needed.

## Error diagnosis

Failed reads return `{ error, exceptionType, inner, stackTrace }`. Check
`exceptionType`:

| `exceptionType` | Meaning | Fix |
|---|---|---|
| `ClientSyncTimeOut` | Read/write took longer than the ADS default (5 s) | Increase `timeout_seconds` |
| `ObjectDisposedException` | Session was torn down (e.g. after a VPN drop) | Reconnect (`beckhoff_connect`) |
| `AdsErrorCode 1808` | Path not resolvable | Use `recurse` to find the correct path |

## Connection state after a VPN reconnect

After a VPN reconnect always call `beckhoff_connect` again before reading —
the session may have been invalidated silently.

## Route / transport issues

If `transport='tcp'` connects but reads/writes silently fail or time out,
the PLC likely dropped the AMS frames because it has no backroute to your
local AmsNetId — see
[`beckhoff_add_route`](tools-reference.md#beckhoff_add_route--registering-a-backroute-windows-only).
