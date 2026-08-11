# Specification

- **[v1.md](v1.md)** — the wire format. Normative for every client library in this repository.

## Relationship to the NetCrunch product documentation

NetCrunch's receiver accepts more than this specification describes, across JSON, XML and CSV, and
serves ingestion paths that have nothing to do with these libraries. `v1.md` is the deliberately
narrow subset that client libraries commit to — chosen so that the payloads a library emits stay
valid as the receiver evolves.

Where the two disagree about what the receiver *accepts*, the product documentation is right. Where
they disagree about what a **client library** should *emit*, `v1.md` is right.

## Changing the format

The format is a contract with code running inside other people's applications. See the compatibility
section of `v1.md`, and [CONTRIBUTING.md](../CONTRIBUTING.md) for the process.
