Feature: Gateway contract

  Domain-level E2E. A caller reaches the sample workflow through the APIM emulator front door.
  The emulator enforces the gateway contract (subscription key, body shape, health); a valid
  request is then processed by the Logic App and published to Service Bus. Copy this as the
  starting point for your own cross-Logic-App / APIM E2E scenarios.

  Scenario: Health endpoint returns an acknowledgement
    When a client calls the gateway health endpoint
    Then the gateway response status is 200

  Scenario: Missing subscription key is rejected at the gateway
    When a client POSTs the order "sample-order.json" to the gateway without a subscription key
    Then the gateway response status is 401

  Scenario: Malformed body is rejected at the gateway
    When a client POSTs a malformed body to the gateway
    Then the gateway response status is 400

  Scenario: A valid order flows through the gateway and Logic App to the queue
    Given the external enrichment API returns 200 with an enriched order
    When a valid order "sample-order.json" is sent through the gateway and Logic App
    Then the gateway response status is 202
    And a message is published to the sample queue
