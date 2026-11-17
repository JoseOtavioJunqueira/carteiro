# 1. PostgreSQL as the delivery queue

Status: accepted

## Context

Deliveries need a work queue: something that holds due work, lets several workers take items
without taking the same one twice, and survives restarts. The usual answer is a message broker
(RabbitMQ, SQS, Kafka).

Carteiro already needs PostgreSQL for endpoints, events and the delivery history that the API
exposes. Adding a broker means a second stateful system to run, and, worse, two places where the
state of a delivery lives. Accepting an event would then mean "write to the database and publish to
the broker", which is the dual-write problem the outbox pattern exists to solve.

## Decision

The `deliveries` table is the queue. Workers claim due rows with:

```sql
WITH due AS (
    SELECT d.id FROM deliveries d JOIN endpoints e ON e.id = d.endpoint_id
    WHERE d.status IN ('Pending', 'Failed')
      AND d.next_attempt_at <= @now
      AND (d.locked_until IS NULL OR d.locked_until <= @now)
      AND e.enabled
    ORDER BY d.next_attempt_at
    LIMIT @batch
    FOR UPDATE OF d SKIP LOCKED
)
UPDATE deliveries SET locked_until = @lease_end, lock_token = @token
FROM due WHERE deliveries.id = due.id
RETURNING deliveries.*;
```

`FOR UPDATE SKIP LOCKED` makes concurrent workers skip rows another transaction has locked
instead of waiting on them, so they get disjoint batches. A partial index on `next_attempt_at`
(`WHERE status IN ('Pending', 'Failed')`) keeps the scan limited to work that is still open.

Retry scheduling is just `next_attempt_at`; there is no separate delay queue.

## Consequences

- One system to operate, back up and reason about. A delivery's state, its history and its place
  in the queue are the same row.
- Workers poll. An idle worker issues one cheap indexed query per `PollInterval` (1 s by default).
  `LISTEN/NOTIFY` could cut latency, but it was not needed.
- Throughput is bounded by what one PostgreSQL primary can do. For a webhook service, where the
  slow part is waiting on other people's HTTP servers, that limit is far away; if it ever stops
  being far away, the queue can move to a broker while the tables stay as the record.
- The `locked_until` check alone is not enough. With the row lock removed (keeping the
  `locked_until` filter), both tests in `ConcurrentWorkerTests` failed in each of three runs: two
  workers read the same snapshot, both see the row as free, and both update it. The lock is what
  makes the second worker skip the row.
