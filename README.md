# BackgroundTaskQueue.AspNetCore

[![NuGet Version](https://img.shields.io/nuget/v/BackgroundTaskQueue.AspNetCore)](https://www.nuget.org/packages/BackgroundTaskQueue.AspNetCore)

A simple and scalable way to queue fire-and-forget background work in ASP.NET Core using dependency injection, scoped lifetimes, and category-based parallelism.

This service enables background work execution without requiring hosted services in your own code, while supporting graceful shutdown, cancellation, and error handling.

## Features

- Scoped dependency injection for background tasks  
- Optional named categories with configurable concurrency and queueing  
- Bounded capacity with task rejection  
- Graceful cancellation on shutdown  
- Customizable exception logging via `IOffloadWorkExceptionLogger`
- Agressive unit testing

## Installation

Register the service with default configuration:

```csharp
// Defaults to a MaxDegreeOfParallelism of 3, unbounded capacity
builder.Services.AddOffloadWorkService();
```

Or customize the default configuration:

```csharp
builder.Services.AddOffloadWorkService((OffloadWorkServiceOptions opt) =>
{
    // Controls how many tasks can run concurrently
    opt.MaxDegreeOfParallelism = 5;
    // Limits the number of active tasks (-1 for unlimited)
    opt.BoundedCapacity = 100;
});
```

Or define multiple named categories:

```csharp
builder.Services.AddOffloadWorkService((OffloadWorkServiceCategoryOptions cat) =>
{
    cat.AddCategory("email", new OffloadWorkServiceOptions
    {
        MaxDegreeOfParallelism = 2,
        BoundedCapacity = 10
    });

    cat.AddCategory("pdf", new OffloadWorkServiceOptions
    {
        MaxDegreeOfParallelism = 4,
        BoundedCapacity = -1 // unbounded
    });
});
```

## Usage

Inject `IOffloadWorkService` into your controller or service.

```csharp
public sealed class MyController: ControllerBase
{
    private readonly IOffloadWorkService _offloader;

    public MyController(IOffloadWorkService offloadWorkService)
    {
        _offloader = offloadWorkService;
    }
}
```

**Offload to the default category:**

```csharp
[HttpPost, Route("reindex")]
public IActionResult Reindex()
{
    var accepted = _offloader.Offload(async (sp, _, ct) =>
    {
        var search = sp.GetRequiredService<ISearchIndexer>();
        await search.ReindexAsync(ct);
    }, param: default);

    return accepted ? Accepted() :
        StatusCode(StatusCodes.Status429TooManyRequests, "Queue full");
}
```

**Offload to a named category:**

```csharp
[HttpPost, Route("send-email")]
public IActionResult SendEmail([FromBody] EmailRequest request)
{
    var accepted = _offloader.Offload("email", async (sp, data, ct) =>
    {
        var sender = sp.GetRequiredService<IEmailSender>();
        await sender.SendAsync(data.To, data.Subject, data.Body, ct);
    }, request);

    return accepted ? Accepted() :
        StatusCode(StatusCodes.Status429TooManyRequests, "Email queue full");
}
```

### Checking Queue Length

You can also retrieve the current length of the task queue for monitoring or logging purposes:

```csharp
var queueLength = _offloader.GetActiveCount();
var queueLengthEmail = _offloader.GetActiveCount("email");
```

## Custom Exception Logging

Implement a custom logger to capture exceptions from background tasks.

```csharp
public sealed class MyExceptionLogger : IOffloadWorkExceptionLogger
{
    public void Log(Exception ex, string? category)
    {
        // Send to telemetry, logger, etc.
    }
}
```

Register it:

```csharp
builder.Services.AddTransient<IOffloadWorkExceptionLogger, MyExceptionLogger>();
```

If registered, it will be used in place of the default `ILogger<OffloadWorkService>` for exceptions.

## Behavior

- `BoundedCapacity` is total active + queued items
- Offload returns `false` if the queue is full
- Background tasks receive a scoped `IServiceProvider`
- Cancellation tokens are honored during shutdown
- Logging is customizable but falls back to `ILogger` if needed 

## Clean Shutdown

The service cancels all running and queued tasks during shutdown using linked cancellation tokens. Resources are disposed automatically.

## Contributing
Contributions are welcome! If you have improvements or bug fixes, please open an issue or submit a pull request.

## License
This project is licensed under the [MIT License](LICENSE.txt).