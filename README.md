# Justified Complexity

Companion code for *Justified Complexity: Building Backends One Decision at a Time* and the articles at [nstanev.com](https://nstanev.com).

Each sample shows a backend decision you can run: the simple version, where it breaks, and the fix that justifies the extra complexity. Every sample is self-contained, with its own README and a `compose.yaml`, so Docker is all you need to try it.

## Samples

| Sample | What it shows | Article |
| --- | --- | --- |
| [constraint-beats-validator](samples/constraint-beats-validator) | Async "is this email taken?" validation lets concurrent duplicates through; a unique index plus a 409 doesn't | A database constraint beats an async validator |
| [status-code-policy](samples/status-code-policy) | One endpoint refuses a signup three ways (400, 409, 422) through one error catalogue with stable Problem Details types | 400 or 422? |
| [duplicate-charge](samples/duplicate-charge) | A retried payment charges the card again; an Idempotency-Key, passed on to the payment provider, makes the retry return the first response | The duplicate charge |

## Stack

.NET 10, ASP.NET Core Minimal APIs, EF Core, PostgreSQL, Docker Compose.

## License

Code in this repository is licensed under [MIT](LICENSE). The text of the book *Justified Complexity* and the articles at nstanev.com are © Nikolay Stanev, all rights reserved.
