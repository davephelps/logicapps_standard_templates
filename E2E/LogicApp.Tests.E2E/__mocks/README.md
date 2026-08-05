# `__mocks/`

Static WireMock stub-mapping files (`*.json`) for the **backend** WireMock server (the external
HTTP dependencies the Logic App calls). Auto-loaded before each scenario. Non-`.json` files (like
this README) are ignored.

This is separate from the APIM emulator, which is configured in code (`Infrastructure/ApimEmulator.cs`)
and models the gateway front door, not backend dependencies.
