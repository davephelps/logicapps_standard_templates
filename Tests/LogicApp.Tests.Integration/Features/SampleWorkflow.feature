Feature: Sample order workflow

  Integration-harness smoke test. Drives the sample-order-servicebus workflow end to end
  through the local stack -- func host + Azurite + Service Bus emulator + WireMock. Copy this
  file as the starting point for your own per-workflow integration scenarios.

  Scenario: A valid order is enriched over HTTP and published to the queue
    Given the external enrichment API returns 200 with an enriched order
    When the caller POSTs the order "sample-order.json" to the sample workflow
    Then the caller response status is 200
    And a message is published to the sample queue
