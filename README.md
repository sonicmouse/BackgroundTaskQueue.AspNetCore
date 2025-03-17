# BackgroundTaskQueue.AspNetCore

[![NuGet Version](https://img.shields.io/nuget/v/BackgroundTaskQueue.AspNetCore)](https://www.nuget.org/packages/BackgroundTaskQueue.AspNetCore/1.0.0)

_BackgroundTaskQueue.AspNetCore_ is a lightweight library that provides a robust background task queue for ASP.NET Core applications. It leverages the Task Parallel Library (TPL) Dataflow (specifically, `BufferBlock` and `ActionBlock`) to efficiently queue and process work items in the background, decoupling task production from execution.

## Features

- **Asynchronous Task Offloading:** Easily offload work from transient components (e.g., controllers, services) to a shared background worker.
- **Scoped Dependency Resolution:** Each queued work item executes within its own DI scope, ensuring that services are correctly instantiated and disposed.
- **Controlled Concurrency:** Configure the maximum degree of parallelism to limit concurrent execution.
- **Cancellation Support:** Work items respect cancellation tokens, ensuring graceful shutdown of the background processing.
- **Queue Monitoring:** Get insight into the current queue length.

## Getting Started

### Installation

Clone this repository or add the project to your solution. Then, register the service using the provided extension method.

## Usage

### Registering the Service

In your `Program.cs` or `Startup.cs`, register the background task queue service with optional configuration settings. The example below demonstrates how to add the service and customize options such as the maximum degree of parallelism and bounded capacity:

```csharp
// In Program.cs or Startup.cs
builder.Services.AddOffloadWorkService(options =>
{
    options.MaxDegreeOfParallelism = 5; // Controls how many tasks can run concurrently
    options.BoundedCapacity = -1; // Limits the number of enqueued tasks (-1 for unlimited)
});
```

### Default Configuration
- **MaxDegreeOfParallelism**: `3` (Only three tasks run concurrently)
- **BoundedCapacity**: `-1` (Unlimited queue capacity)

The `MaxDegreeOfParallelism` setting limits the number of tasks executing **simultaneously**, but does **not** restrict the total number of tasks that can be enqueued. The `BoundedCapacity` setting defines the maximum number of tasks that can be queued before rejecting additional work.

### Offloading Work

Inject the `IOffloadWorkService` into your controller or service and use it to queue work. Each work item receives the current `IServiceProvider` and a cancellation token, making it safe to resolve scoped services.

```csharp
using Microsoft.AspNetCore.Mvc;
using BackgroundTaskQueue.AspNetCore;

[ApiController]
[Route("[controller]")]
public class WorkController : ControllerBase
{
    private readonly IOffloadWorkService _offloadWorkService;

    public WorkController(IOffloadWorkService offloadWorkService)
    {
        _offloadWorkService = offloadWorkService;
    }

    [HttpPost("do-work")]
    public IActionResult DoWork([FromQuery] string param)
    {
        // Offload a background task
        var accepted = _offloadWorkService.Offload(async (serviceProvider, myParam, cancellationToken) =>
        {
            // Resolve a service
            var myService = serviceProvider.GetRequiredService<IMyService>();

            // Execute some work asynchronously
            await myService.DoWorkAsync(myParam, cancellationToken);
        }, param);

        if (accepted)
        {
            return Accepted();
        }

        // The BoundedCapacity of the queue has been reached. Let caller know.
        return StatusCode(StatusCodes.Status429TooManyRequests, "Task queue is full.");
    }
}
```

### Checking Queue Length

You can also retrieve the current length of the task queue for monitoring or logging purposes:

```csharp
int queueLength = _offloadWorkService.GetQueueLength();
```

## How It Works

The core of the library is the `OffloadWorkService`, which implements both `IOffloadWorkService` and `IHostedService` (via `BackgroundService`). It uses:

- `BufferBlock<Func<Task>>`: Acts as the work queue. Each enqueued work item is a lambda (a `Func<Task>`) that encapsulates the work to be performed.
- `ActionBlock<Func<Task>>`: Processes the queued work items with a configurable maximum degree of parallelism. It ensures that each task runs safely within its own dependency injection scope.
- **Cancellation Tokens**: The service creates a linked cancellation token combining the ASP.NET Core host’s token and an internal token. This ensures that tasks respect shutdown signals.
- **Scoped Work Execution**: Each work item is executed by first creating an `AsyncServiceScope` (using the injected `IServiceScopeFactory`), ensuring that all DI dependencies are properly resolved and disposed after use.

## Contributing
Contributions are welcome! If you have improvements or bug fixes, please open an issue or submit a pull request.

## License
This project is licensed under the [MIT License](LICENSE.txt).

