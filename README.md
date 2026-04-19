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

await client.InsertAsync("mydb.users", "{\"id\":\"u1\",\"name\":\"John\",\"age\":30}");

string rows = await client.OnqlAsync("select * from mydb.users where age > 18");
Console.WriteLine(rows);

await client.UpdateAsync("mydb.users.u1", "{\"age\":31}");
await client.DeleteAsync("mydb.users.u1");
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

Because the driver is dependency-free, every JSON-valued parameter (record,
ctxvalues) is passed as a **pre-serialized JSON string**. Use
`System.Text.Json`, Newtonsoft.Json, or any library you like to serialize.

The `path` argument is a **dotted string**:

| Path shape | Meaning |
|------------|---------|
| `"mydb.users"` | Table (used by `InsertAsync`) |
| `"mydb.users.u1"` | Record id `u1` (used by `UpdateAsync` / `DeleteAsync`) |

### `await client.InsertAsync(path, recordJson)`

Insert a **single** record.

```csharp
await client.InsertAsync("mydb.users",
    "{\"id\":\"u1\",\"name\":\"John\",\"age\":30}");
```

### `await client.UpdateAsync(path, recordJson)` / `client.UpdateAsync(path, recordJson, protopass)`

Update the record at `path`.

```csharp
await client.UpdateAsync("mydb.users.u1", "{\"age\":31}");
await client.UpdateAsync("mydb.users.u1", "{\"active\":false}", "admin");
```

### `await client.DeleteAsync(path)` / `client.DeleteAsync(path, protopass)`

Delete the record at `path`.

```csharp
await client.DeleteAsync("mydb.users.u1");
```

### `await client.OnqlAsync(query)` / `client.OnqlAsync(query, protopass, ctxkey, ctxvaluesJson)`

Run a raw ONQL query.

```csharp
string data = await client.OnqlAsync("select * from mydb.users where age > 18");
```

### `client.Build(query, params object[] values)`

Replace `$1`, `$2`, … placeholders with values. Strings are automatically
double-quoted; numbers and booleans are inlined verbatim.

```csharp
string q = client.Build(
    "select * from mydb.users where name = $1 and age > $2",
    "John", 18);
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
