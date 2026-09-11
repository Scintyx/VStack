# VStack v7 Raw Wire Diagnostic

This build does not add another speculative fix.

It keeps:
- the working stack multiplier hook,
- the 3 serializer / 9-bound patch,
- the safe formatter observation.

It removes the v6 value filter from the two small bounded integer diagnostic hooks.
The first 80 calls to each helper are logged exactly as received.

## Why

v6 installed both hooks successfully, but no filtered wire lines appeared. That means either:

1. the live calls use different bounds/values than assumed,
2. the argument ordering differs from our assumption, or
3. these helpers are not used by the live inventory path.

v7 distinguishes those cases.

## Test

1. Install the same v7 DLL on client and server.
2. Fully restart both.
3. Join the server.
4. Use a real stack above 4095 (for example 6109).
5. Open inventory and leave it open briefly.
6. Send the client `LogOutput.log`.
7. If using a separate dedicated server, also send the server `LogOutput.log`.

Look for:

- `VSTACK-V7 RAW-WRITE PRE/POST`
- `VSTACK-V7 RAW-READ PRE/POST`
- `Inventory stack formatter received exactly 4095`

If RAW-READ/WRITE remain completely absent, those helper targets are not the active path and we will stop using them.
