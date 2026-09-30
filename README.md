# Wolverine: envelopes executed twice after a node starts on a PostgreSQL queue backlog

With Wolverine's default durability settings (Balanced mode, durability agent enabled, durable
inbox), a node that starts while its PostgreSQL queue already holds messages executes part of that
backlog twice, with the same envelope id. One node is enough.

Reproduced with Wolverine 6.39.1 (set `WolverineVersion` in `ReproApp/ReproApp.csproj` to try
another version).

## Run it

Requirements: .NET 10 SDK, the [Aspire CLI](https://aspire.dev/get-started/install-cli/) and a
container runtime supported by Aspire (Docker or Podman).

From the repository root:

```bash
aspire start && aspire wait repro --status down --timeout 300 && aspire logs repro; aspire stop
```

`aspire start` starts PostgreSQL 18.3 and the `repro` app in the background. `aspire wait` returns
when `repro` exits, about 90 seconds later. `aspire logs` then prints its output, which ends with
`REPRODUCED` when at least one envelope was executed more than once, and `aspire stop` shuts
everything down.

The race has a window of a few milliseconds on a laptop, so a run can miss it: 12 of 14 local runs
reproduced. When it misses, no `Released ... orphaned messages` line shows up and the app prints
`NOT REPRODUCED`. Run the command again.

## What the repro does

1. A short-lived `sender` node publishes 30 messages to the PostgreSQL queue `repro`, then stops.
2. A node starts on that backlog. Its listener runs one message at a time and the handler takes
   2 s, so its first batch is still in flight when the orphaned-message sweep runs. That happens 15
   to 20 s after startup, once the node has taken leadership and started its durability agent.
   Everything else is default configuration.
3. The app records every execution with its envelope id, and lists the envelopes executed more than
   once.

## Expected output

Trimmed: the `[repro]` prefix that `aspire logs` adds to each line, and Wolverine's other startup
and shutdown lines, are left out.

```text
19:48:14.851 30 messages waiting in the queue, starting the node
19:48:14.986 info: Wolverine.Runtime.WolverineRuntime[0] Wolverine assigned node id for envelope persistence is -992170819
19:48:15.018 info: Wolverine.Transports.ListeningAgent[0] Started message listening at postgresql://repro/
19:48:15.025 info: Wolverine.Runtime.Agents.NodeAgentController[0] Starting agents for Node 5d035643-0ebe-42e1-990b-fa010a2ae3c8 with assigned node id 2 and Control Uri dbcontrol://5d035643-0ebe-42e1-990b-fa010a2ae3c8/
19:48:28.077 info: Wolverine.Runtime.Agents.NodeAgentController[0] Node 2 (5d035643-0ebe-42e1-990b-fa010a2ae3c8) successfully assumed leadership
19:48:28.095 info: Wolverine.Runtime.Agents.NodeAgentController[0] Successfully started agent wolverinedb://postgresql/localhost/wolverine/repro_store on Node 2
19:48:31.390 info: Wolverine.RDBMS.DurabilityAgent[0] Released 20 orphaned messages in repro_store.wolverine_incoming_envelopes of database default previously owned by departed nodes -992170819
19:48:36.365 info: Wolverine.RDBMS.DurabilityAgent[0] Issuing a command to recover 10 incoming messages from the inbox to destination postgresql://repro/
19:48:36.377 info: Wolverine.RDBMS.DurabilityAgent[206] Recovered 10 incoming envelopes from storage
19:49:15.704 message #11 (envelope 08df1f1b-00c1-7560-0200-000000000000) is executing again (execution 2)
...
19:49:33.752 message #20 (envelope 08df1f1b-00c3-f0f6-0200-000000000000) is executing again (execution 2)

Published 30 messages, 40 executions
Messages executed more than once: 10
  #11 envelope 08df1f1b-00c1-7560-0200-000000000000: executed at 19:48:35.588 and 19:49:15.704
  ...
  #20 envelope 08df1f1b-00c3-f0f6-0200-000000000000: executed at 19:48:53.642 and 19:49:33.752
REPRODUCED
```
