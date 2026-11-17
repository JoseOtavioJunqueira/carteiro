# 3. Fan-out inside the ingest transaction

Status: accepted

## Context

When `POST /events` returns success, the producer considers the event handed over. If the service
then crashes before it has recorded which endpoints must receive the event, the event is silently
lost. That is the failure the transactional outbox pattern is about.

Two options:

1. Store the event, return, and fan out later in the background (a second step that reads new
   events and creates deliveries).
2. Create the event and all of its delivery rows in the same database transaction as the request.

## Decision

Option 2. `POST /events` looks up the enabled, subscribed endpoints, then inserts the event and one
`deliveries` row per endpoint with a single `SaveChanges`, which EF Core runs in one transaction.
The delivery rows *are* the outbox: the worker reads them directly, so there is no relay process
and nothing to publish after the commit.

Idempotency keys ride on the same transaction. `events.idempotency_key` has a unique index. A
retried request with the same key either finds the existing event up front, or, if it raced
another request with that key, fails the insert on the unique index, rolls back its whole
transaction (event and deliveries) and returns the winner's event. The integration test
`Concurrent_requests_with_the_same_key_create_exactly_one_event` sends 20 simultaneous requests
with one key and checks that exactly one event and one set of deliveries exist.

## Consequences

- An accepted event always has its deliveries. `The_event_and_its_deliveries_are_committed_atomically`
  forces the second delivery insert to fail with a trigger and checks that nothing was committed.
- The subscription set is fixed at ingest time. An endpoint created a second after an event was
  accepted does not get that event. This is the behaviour most webhook providers have, and it
  makes deliveries reproducible.
- Ingest latency grows with the number of subscribers, since each one is a row in the same
  transaction. Fine for tens of endpoints per event type; for thousands, fan-out would move to a
  background step (option 1, with the event row itself as the outbox).
- Reusing an idempotency key with a different type or payload returns 422 instead of silently
  returning the old event, because that is almost always a client bug.
