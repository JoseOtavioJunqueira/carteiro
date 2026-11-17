# 2. At-least-once delivery

Status: accepted

## Context

A webhook is an HTTP request to a server we do not control. If the connection drops after the
receiver processed the request but before we read the response, we cannot know whether it was
processed. Any guarantee we offer has to account for that.

- **At-most-once**: send once, never retry. Simple, and loses events whenever anything fails.
- **Exactly-once**: not achievable across an HTTP boundary without the receiver's cooperation.
  Some systems advertise it, but they mean at-least-once plus deduplication on the receiving side.
- **At-least-once**: retry until we get a 2xx (or give up). No event is lost while retries last,
  but the receiver may see the same delivery more than once.

## Decision

Carteiro delivers at least once, and makes deduplication easy for receivers:

- Every request carries `Carteiro-Delivery-Id`, which is stable across all attempts of the same
  delivery (event × endpoint). A receiver that records processed delivery ids can drop repeats.
- `Carteiro-Attempt` tells the receiver which attempt this is, mostly for logs.

Duplicates can happen when:

1. The receiver processed the request but answered after the timeout, or answered non-2xx.
2. A worker crashed after sending and before recording the result. The lease expires and another
   worker sends again.
3. An operator manually retries a dead delivery that the receiver had in fact processed.

## Consequences

- No lost events while the retry budget lasts, and dead-lettered deliveries stay visible and can
  be retried by hand.
- Receivers must be idempotent. The README and the sample receiver say so explicitly.
- Ordering is not guaranteed. Retries and parallel workers mean event B can arrive before event A.
  Receivers that care should use timestamps or versions in the payload.
