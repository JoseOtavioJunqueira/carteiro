# 4. Leases and fencing tokens for claimed deliveries

Status: accepted

## Context

The simplest way to use `SELECT ... FOR UPDATE SKIP LOCKED` is to keep the transaction open while
the work is done: lock the row, make the HTTP call, update the row, commit. For webhooks that
means holding a database transaction, and a pooled connection, for up to the full request timeout
of a server we do not control. Twenty slow receivers would pin twenty connections.

## Decision

Processing a delivery is split into three steps, and only the first and last touch the database:

1. **Claim** (short transaction): the `SKIP LOCKED` query sets `locked_until = now + ClaimLease`
   and a fresh `lock_token` on the batch, then commits. The row locks are released; the lease is
   what keeps other workers away from now on.
2. **Send**: the HTTP request, with no transaction or connection held.
3. **Complete** (short transaction): `SELECT ... FOR UPDATE` the delivery, check that `lock_token`
   is still ours, record the attempt, move the state machine, clear the lease, commit.

The check in step 3 is a fencing token. If a worker stalls past its lease (GC pause, network
partition to the database), another worker can claim the row under a new token and finish it.
When the first worker wakes up, its token no longer matches and its result is discarded instead
of overwriting the newer state.

`ClaimLease` must be longer than `RequestTimeout`; the options validator refuses to start
otherwise. If it were shorter, every slow-but-successful request would be sent twice.

## Consequences

- Database connections are held for milliseconds per delivery, not seconds.
- A crashed worker delays its claimed deliveries by at most `ClaimLease` (60 s by default) and
  then they are retried. `A_delivery_whose_worker_died_is_picked_up_again_after_the_lease_expires`
  covers this.
- A stalled worker cannot corrupt state, but it may already have sent its request, so the
  receiver can get a duplicate. That is the at-least-once trade-off from ADR 2.
  `A_worker_that_lost_its_lease_cannot_overwrite_the_result` covers the fencing.
- Clocks: lease times come from the application clock, so workers on different hosts need
  reasonably synchronised clocks (NTP). A skew much smaller than `ClaimLease - RequestTimeout`
  is harmless.
