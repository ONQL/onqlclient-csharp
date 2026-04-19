# ONQL C# Driver

Official C# / .NET client for the ONQL database server.

## Installation

### From NuGet

```bash
dotnet add package onql-client
```

Or in a `.csproj`:

```xml
<PackageReference Include="onql-client" Version="0.1.0" />
```

### From source (clone + reference)

```bash
git clone https://github.com/ONQL/onqlclient-csharp.git
dotnet add reference ./onqlclient-csharp/ONQL.Client.csproj
```

## Quick Start

```csharp
using ONQL;

var client = await ONQLClient.CreateAsync("localhost", 5656);

// Execute a query
var result = await client.SendRequestAsync("onql",
    JsonSerializer.Serialize(new {
        db = "mydb",
        table = "users",
        query = "name = \"John\""
    }));
Console.WriteLine(result.Payload);

// Subscribe to live updates
var rid = await client.SubscribeAsync("", "name = \"John\"", (id, keyword, payload) => {
    Console.WriteLine($"Update: {payload}");
});

// Unsubscribe
await client.UnsubscribeAsync(rid);

await client.CloseAsync();
```

## API Reference

### `ONQLClient.CreateAsync(host, port, options)`

Creates and returns a connected client instance.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `host` | `string` | `"localhost"` | Server hostname |
| `port` | `int` | `5656` | Server port |
| `timeout` | `TimeSpan` | `10s` | Default request timeout |

### `client.SendRequestAsync(keyword, payload, timeout?)`

Sends a request and waits for a response.

### `client.SubscribeAsync(onquery, query, callback)`

Opens a streaming subscription. Returns the subscription ID.

### `client.UnsubscribeAsync(rid)`

Stops receiving events for a subscription.

### `client.CloseAsync()`

Closes the connection.

## Direct ORM-style API

On top of raw `SendRequestAsync`, the client exposes convenience methods for
the common `Insert` / `Update` / `Delete` / `Onql` operations. Each one
builds the standard payload envelope for you and parses the `{error, data}`
server envelope — throwing `InvalidOperationException` on a non-empty `error`
field, returning the raw `data` substring on success.

Because the driver is dependency-free, every JSON-valued parameter (records,
query, ids, ctxvalues) is passed as a **pre-serialized JSON string**. Use
`System.Text.Json`, Newtonsoft.Json, or any library you like to serialize.

Call `client.Setup(db)` once to bind a default database name; every
subsequent `InsertAsync` / `UpdateAsync` / `DeleteAsync` / `OnqlAsync` call
will use it.

### `client.Setup(db)`

Sets the default database. Returns `this`, so calls can be chained.

```csharp
client.Setup("mydb");
```

### `await client.InsertAsync(table, recordsJson)`

Insert one record or an array of records.

| Parameter | Type | Description |
|-----------|------|-------------|
| `table` | `string` | Target table |
| `recordsJson` | `string` | JSON object, or array of objects |

Returns the raw `data` substring of the server envelope.

```csharp
await client.InsertAsync("users", "{\"name\":\"John\",\"age\":30}");
await client.InsertAsync("users", "[{\"name\":\"A\"},{\"name\":\"B\"}]");
```

### `await client.UpdateAsync(table, recordsJson, queryJson)` / `client.UpdateAsync(table, recordsJson, queryJson, protopass, idsJson)`

Update records matching `queryJson`.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `table` | `string` | — | Target table |
| `recordsJson` | `string` | — | JSON object of fields to update |
| `queryJson` | `string` | — | JSON query |
| `protopass` | `string` | `"default"` | Proto-pass profile |
| `idsJson` | `string` | `"[]"` | JSON array of record IDs |

```csharp
await client.UpdateAsync("users", "{\"age\":31}", "{\"name\":\"John\"}");
await client.UpdateAsync("users", "{\"active\":false}", "{\"id\":\"u1\"}", "admin", "[]");
```

### `await client.DeleteAsync(table, queryJson)` / `client.DeleteAsync(table, queryJson, protopass, idsJson)`

Delete records matching `queryJson`. Same semantics as `UpdateAsync`.

```csharp
await client.DeleteAsync("users", "{\"active\":false}");
```

### `await client.OnqlAsync(query)` / `client.OnqlAsync(query, protopass, ctxkey, ctxvaluesJson)`

Run a raw ONQL query.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `query` | `string` | — | ONQL query text |
| `protopass` | `string` | `"default"` | Proto-pass profile |
| `ctxkey` | `string` | `""` | Context key |
| `ctxvaluesJson` | `string` | `"[]"` | JSON array of context values |

```csharp
string data = await client.OnqlAsync("select * from users where age > 18");
```

### `client.Build(query, params object[] values)`

Replace `$1`, `$2`, … placeholders with values. Strings are automatically
double-quoted; numbers and booleans are inlined verbatim.

```csharp
string q = client.Build(
    "select * from users where name = $1 and age > $2",
    "John", 18);
// -> select * from users where name = "John" and age > 18
string data = await client.OnqlAsync(q);
```

### `ONQLClient.ProcessResult(raw)`

Static helper that parses the `{error, data}` envelope. Throws
`InvalidOperationException` on non-empty `error`; returns the raw `data`
substring on success. Useful when you prefer to build payloads yourself.

### Full example

```csharp
using ONQL;

var client = await ONQLClient.CreateAsync("localhost", 5656);
client.Setup("mydb");

await client.InsertAsync("users", "{\"name\":\"John\",\"age\":30}");

string rows = await client.OnqlAsync(
    client.Build("select * from users where age >= $1", 18)
);
Console.WriteLine(rows);

await client.UpdateAsync("users", "{\"age\":31}", "{\"name\":\"John\"}");
await client.DeleteAsync("users", "{\"name\":\"John\"}");
await client.CloseAsync();
```

## Protocol

The client communicates over TCP using a delimiter-based message format:

```
<request_id>\x1E<keyword>\x1E<payload>\x04
```

- `\x1E` — field delimiter
- `\x04` — end-of-message marker

## License

MIT
