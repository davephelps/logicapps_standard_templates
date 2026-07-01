# `__mocks/`

Static WireMock stub-mapping files (`*.json`) that are auto-loaded before each scenario by
`WorkflowHost_Hooks.StartWireMock` via `WireMockSetup.LoadStubs`. Only WireMock mapping JSON
belongs here -- non-`.json` files (like this README) are ignored by `ReadStaticMappings`.

Use static mappings for large/reusable stub bodies. For simple, scenario-specific stubs, prefer
programmatic stubs in `[Given]` steps (see `Steps/CommonSteps.cs`).

WireMock stubs **external HTTP only**. Internal Azure connectors (Service Bus, Blob, Queue,
Table, SFTP) must hit the emulator stack, never WireMock.
