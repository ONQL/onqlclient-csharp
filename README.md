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

await client.InsertAsync("mydb", "users",
    "{\"id\":\"u1\",\"name\":\"John\",\"age\":30}");

string rows = await client.OnqlAsync("mydb.users[age>18]");
Console.WriteLine(rows);

await client.UpdateAsync("mydb", "users", "{\"age\":31}",
    client.Build("mydb.users[id=$1].id", "u1"));

await client.DeleteAsync("mydb", "users", "", "default", "[\"u1\"]");

await client.CloseAsync();
```

## API Reference

### `ONQLClient.CreateAsync(host, port, timeoutSeconds)`

Creates and returns a connected client instance.

### `client.SendRequestAsync(keyword, payload, timeoutMs?)`

Sends a raw request frame and waits for a response.

### `client.CloseAsync()`

Closes the connection.

## Direct ORM-style API

On top of raw `SendRequestAsync`, the client exposes convenience methods for
the `Insert` / `Update` / `Delete` / `Onql` operations. Each one builds the
standard payload envelope for you and parses the `{error, data}` envelope —
throwing `InvalidOperationException` on a non-empty `error`, returning the
raw `data` substring on success.

Because the driver is dependency-free, every JSON-valued parameter is passed
as a **pre-serialized JSON string**. Use `System.Text.Json`, Newtonsoft.Json,
or any library you like to serialize.

`db` is passed explicitly to `Insert` / `Update` / `Delete`. `Onql` takes a
fully-qualified ONQL expression.

`query` arguments are **ONQL expression strings**, e.g.
`mydb.users[id="u1"].id`.

### `await client.InsertAsync(db, table, recordJson)`

Insert a **single** record.

```csharp
await client.InsertAsync("mydb", "users",
    "{\"id\":\"u1\",\"name\":\"John\",\"age\":30}");
```

### `await client.UpdateAsync(db, table, recordJson, query)` / `client.UpdateAsync(db, table, recordJson, query, protopass, idsJson)`

Update records matching `query` (or `idsJson`). Pass `""` for `query` when
using `idsJson`.

```csharp
await client.UpdateAsync("mydb", "users", "{\"age\":31}",
    client.Build("mydb.users[id=$1].id", "u1"));

await client.UpdateAsync("mydb", "users", "{\"active\":false}",
    "", "default", "[\"u1\"]");
```

### `await client.DeleteAsync(db, table, query)` / `client.DeleteAsync(db, table, query, protopass, idsJson)`

Delete records matching `query` (or `idsJson`).

```csharp
await client.DeleteAsync("mydb", "users",
    client.Build("mydb.users[id=$1].id", "u1"));

await client.DeleteAsync("mydb", "users", "", "default", "[\"u1\"]");
```

### `await client.OnqlAsync(query)` / `client.OnqlAsync(query, protopass, ctxkey, ctxvaluesJson)`

Run a raw ONQL query.

```csharp
string data = await client.OnqlAsync("mydb.users[age>18]");
```

### `client.Build(query, params object[] values)`

Replace `$1`, `$2`, … placeholders with values.

```csharp
string q = client.Build("mydb.users[name=$1 and age>$2]", "John", 18);
string data = await client.OnqlAsync(q);
```

### `ONQLClient.ProcessResult(raw)`

Static helper that parses the `{error, data}` envelope.

## Protocol

```
<request_id>\x1E<keyword>\x1E<payload>\x04
```

- `\x1E` — field delimiter
- `\x04` — end-of-message marker

## License

MIT
