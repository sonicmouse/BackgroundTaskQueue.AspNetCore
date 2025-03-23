using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace BackgroundTaskQueue.AspNetCore.Tests
{
	public sealed class OffloadWorkServiceTests
	{
		private static async Task RunWithOffloadServiceAsync(
			Func<ServiceCollection, CancellationToken> configure,
			Func<IOffloadWorkService, ITestLogger, Task> test)
		{
			var services = new ServiceCollection();
			var logger = services.AddCustomLogger<ITestLogger>(typeof(TestLogger<>));
			var ct = configure(services);

			var provider = services.BuildServiceProvider();
			var offloadWorkService = provider.GetRequiredService<IOffloadWorkService>();
			var tBgServiceRunner = provider.StartHostedOffloadWorkServiceAsync(ct);

			await test(offloadWorkService, logger);

			await provider.StopHostedOffloadWorkServiceAsync(CancellationToken.None);
			await tBgServiceRunner;
			Assert.True(tBgServiceRunner.IsCompletedSuccessfully);
		}

		[Fact]
		public Task AddOnce()
		{
			return RunWithOffloadServiceAsync(services =>
			{
				services.AddOffloadWorkService();
				var ex = Assert.Throws<InvalidOperationException>(() =>
					services.AddOffloadWorkService());
				Assert.Equal("AddOffloadWorkService has already been called.", ex.Message);
				return CancellationToken.None;
			},
			(_, _) => Task.CompletedTask);
		}

		[Fact]
		public Task DefaultHappyPath()
		{
			return RunWithOffloadServiceAsync(services =>
			{
				services.AddOffloadWorkService();
				return CancellationToken.None;
			},
			async (service, logger) =>
			{
				var tcs1 = new TaskCompletionSource();
				var tcs2 = new TaskCompletionSource();

				service.Offload((_, param, _) =>
				{
					Assert.Equal("hello", param);
					tcs1.SetResult();
					return Task.CompletedTask;
				}, "hello");

				service.Offload((_, param, _) =>
				{
					Assert.Equal("world", param);
					tcs2.SetResult();
					return Task.CompletedTask;
				}, "world");

				await Task.WhenAll(tcs1.Task, tcs2.Task);
				Assert.Empty(logger.Entries);
			});
		}

		[Fact]
		public Task SingleHappyPath()
		{
			return RunWithOffloadServiceAsync(services =>
			{
				services.AddOffloadWorkService((OffloadWorkServiceOptions b) => { });
				return CancellationToken.None;
			},
			async (service, logger) =>
			{
				var tcs1 = new TaskCompletionSource();
				var tcs2 = new TaskCompletionSource();

				service.Offload((_, param, _) =>
				{
					Assert.Equal("hello", param);
					tcs1.SetResult();
					return Task.CompletedTask;
				}, "hello");

				service.Offload((_, param, _) =>
				{
					Assert.Equal("world", param);
					tcs2.SetResult();
					return Task.CompletedTask;
				}, "world");

				await Task.WhenAll(tcs1.Task, tcs2.Task);
				Assert.Empty(logger.Entries);
			});
		}

		[Fact]
		public Task CategoryHappyPath()
		{
			return RunWithOffloadServiceAsync(services =>
			{
				services.AddOffloadWorkService((OffloadWorkServiceCategoryOptions b) =>
				{
					b.AddCategory("Test Category", new()
					{
						BoundedCapacity = -1,
						MaxDegreeOfParallelism = 3
					});
					b.AddCategory("Another Category", new()
					{
						BoundedCapacity = -1,
						MaxDegreeOfParallelism = 3
					});
				});
				return CancellationToken.None;
			},
			async (service, logger) =>
			{
				var tcs1 = new TaskCompletionSource();
				var tcs2 = new TaskCompletionSource();

				service.Offload("Test Category", (_, param, _) =>
				{
					Assert.Equal("hello", param);
					tcs1.SetResult();
					return Task.CompletedTask;
				}, "hello");

				service.Offload("Another Category", (_, param, _) =>
				{
					Assert.Equal("world", param);
					tcs2.SetResult();
					return Task.CompletedTask;
				}, "world");

				await Task.WhenAll(tcs1.Task, tcs2.Task);
				Assert.Empty(logger.Entries);
			});
		}

		[Fact] public Task ValidateCategoryNotRegisteredError()
		{
			return RunWithOffloadServiceAsync(services =>
			{
				services.AddOffloadWorkService();
				return CancellationToken.None;
			},
			(service, logger) =>
			{
				var ex = Assert.Throws<InvalidOperationException>(() =>
					service.Offload("Not Found", (_, _, _) => Task.CompletedTask, 0));
				Assert.Equal($"The category 'Not Found' is not registered.", ex.Message);

				ex = Assert.Throws<InvalidOperationException>(() =>
					service.GetActiveCount("Not Found"));
				Assert.Equal($"The category 'Not Found' is not registered.", ex.Message);

				Assert.Empty(logger.Entries);
				return Task.CompletedTask;
			});
		}

		[Fact]
		public Task ValidateDefaultNotRegisteredError()
		{
			return RunWithOffloadServiceAsync(services =>
			{
				services.AddOffloadWorkService((OffloadWorkServiceCategoryOptions b) =>
				{
					b.AddCategory("Thing");
				});
				return CancellationToken.None;
			},
			(service, logger) =>
			{
				var ex = Assert.Throws<InvalidOperationException>(() =>
					service.Offload((_, _, _) => Task.CompletedTask, 0));
				Assert.Equal("Offload service is unavailable or not initialized correctly.",
					ex.Message);

				ex = Assert.Throws<InvalidOperationException>(() =>
					service.GetActiveCount());
				Assert.Equal("Offload service is unavailable or not initialized correctly.",
					ex.Message);

				Assert.Empty(logger.Entries);
				return Task.CompletedTask;
			});
		}

		[Fact]
		public Task ZeroBoundedCapacity()
		{
			return RunWithOffloadServiceAsync(services =>
			{
				services.AddOffloadWorkService((OffloadWorkServiceOptions b) =>
				{
					b.BoundedCapacity = 0;
				});
				return CancellationToken.None;
			},
			(service, logger) =>
			{
				Assert.False(service.Offload((_, _, _) => Task.CompletedTask, 0));
				return Task.CompletedTask;
			});
		}

		[Fact]
		public async Task BoundingLimitHit_Cancellation()
		{
			using var cts = new CancellationTokenSource();
			await RunWithOffloadServiceAsync(services =>
			{
				services.AddOffloadWorkService((OffloadWorkServiceOptions b) =>
				{
					b.MaxDegreeOfParallelism = 3;
					b.BoundedCapacity = 2;
				});
				return cts.Token;
			},
			async (service, logger) =>
			{
				var tcs = new TaskCompletionSource();
				var t = service.Offload(async (_, _, ct) =>
				{
					tcs.SetResult();
					await Task.Delay(Timeout.Infinite, ct);
				}, 0);

				Assert.True(t);
				await tcs.Task;

				var tcs2 = new TaskCompletionSource();
				var t2 = service.Offload(async (_, _, ct) =>
				{
					tcs2.SetResult();
					await Task.Delay(Timeout.Infinite, ct);
				}, 0);

				Assert.True(t2);
				await tcs2.Task;

				t = service.Offload((_, _, _) =>
				{
					Assert.Fail();
					return Task.CompletedTask;
				}, 0);

				Assert.Empty(logger.Entries);
				Assert.False(t);
			});
		}

		[Fact]
		public async Task UnlimitedBounding_ActiveCount_Cancellation()
		{
			const int count = 50;
			using var cts = new CancellationTokenSource();
			await RunWithOffloadServiceAsync(services =>
			{
				services.AddOffloadWorkService((OffloadWorkServiceOptions b) =>
				{
					b.BoundedCapacity = -1;
					b.MaxDegreeOfParallelism = count;
				});
				return cts.Token;
			},
			async (service, logger) =>
			{
				var tcs = new TaskCompletionSource[count];
				var tcsC = new TaskCompletionSource[count];

				for(var i = 0; i < count; ++i)
				{
					tcs[i] = new();
					tcsC[i] = new();
					
					var t = service.Offload(async (_, param, ct) =>
					{
						tcs[param].SetResult();
						try
						{
							await Task.Delay(Timeout.Infinite, ct);
						}
						catch (OperationCanceledException)
						{
							tcsC[param].SetResult();
						}
					}, i);
					Assert.True(t);
				}

				Assert.Equal(count, service.GetActiveCount());

				// wait for them to all to get in to a run-state
				await Task.WhenAll(tcs.Select(t => t.Task));
				Assert.Equal(count, service.GetActiveCount());
				// cancel everything
				await cts.CancelAsync();
				// wait for them all to get notified
				await Task.WhenAll(tcsC.Select(t => t.Task));
				// give a little time for them all to spin down
				await Task.Delay(250);

				Assert.Equal(0, service.GetActiveCount());
				Assert.Empty(logger.Entries);
			});
		}

		[Fact]
		public Task ServiceRegistration()
		{
			return RunWithOffloadServiceAsync(services =>
			{
				services.AddOffloadWorkService();
				services.AddTransient<ITestService, TestService>();
				return CancellationToken.None;
			},
			async (service, logger) =>
			{
				var tcs = new TaskCompletionSource<bool>();
				service.Offload((sp, _, _) =>
				{
					try
					{
						Assert.IsAssignableFrom<ITestService>(sp.GetRequiredService<ITestService>());
					}
					catch
					{
						tcs.SetResult(false);
						return Task.CompletedTask;
					}
					tcs.SetResult(true);
					return Task.CompletedTask;
				}, 0);

				Assert.True(await tcs.Task);
				Assert.Empty(logger.Entries);
			});
		}

		private interface ITestService { }
		private sealed class TestService : ITestService { }

		[Fact]
		public Task LogUnhandledExceptionsDefault()
		{
			return RunWithOffloadServiceAsync(services =>
			{
				services.AddOffloadWorkService();
				return CancellationToken.None;
			},
			async (service, logger) =>
			{
				service.Offload((_, _, _) => throw new InvalidProgramException(), 0);
				await Task.Delay(250);

				Assert.Single(logger.Entries);
				Assert.Equal(LogLevel.Error, logger.Entries[0].Level);
				Assert.Equal("[OffloadWorkService] Offloaded work threw an unhandled exception.",
					logger.Entries[0].Message);
				Assert.IsType<InvalidProgramException>(logger.Entries[0].Exception);
			});
		}

		[Fact]
		public Task LogUnhandledExceptionsCategory()
		{
			return RunWithOffloadServiceAsync(services =>
			{
				services.AddOffloadWorkService((OffloadWorkServiceCategoryOptions b) =>
				{
					b.AddCategory("Test");
				});
				return CancellationToken.None;
			},
			async (service, logger) =>
			{
				service.Offload("Test", (_, _, _) => throw new InvalidProgramException(), 0);
				await Task.Delay(250);

				Assert.Single(logger.Entries);
				Assert.Equal(LogLevel.Error, logger.Entries[0].Level);
				Assert.Equal("[OffloadWorkService] Offloaded work threw an unhandled " +
					"exception. (Category: 'Test')", logger.Entries[0].Message);
				Assert.IsType<InvalidProgramException>(logger.Entries[0].Exception);
			});
		}

		[Fact]
		public Task MixedCategoryBoundedCapacities()
		{
			return RunWithOffloadServiceAsync(services =>
			{
				services.AddOffloadWorkService((OffloadWorkServiceCategoryOptions b) =>
				{
					b.AddCategory("Limited", new()
					{
						BoundedCapacity = 1,
						MaxDegreeOfParallelism = 1
					});
					b.AddCategory("Unlimited", new()
					{
						BoundedCapacity = -1,
						MaxDegreeOfParallelism = 5
					});
					b.AddCategory("Disabled", new()
					{
						BoundedCapacity = 0,
						MaxDegreeOfParallelism = 1
					});
				});
				return CancellationToken.None;
			},
			(service, logger) =>
			{
				var acceptedLimited = service.Offload("Limited", (_, _, _)
					=> Task.CompletedTask, 0);
				var rejectedLimited = service.Offload("Limited", (_, _, _)
					=> Task.CompletedTask, 0);

				var acceptedUnlimited = 0;
				for (int i = 0; i < 10; ++i)
				{
					if (service.Offload("Unlimited", (_, _, _) => Task.CompletedTask, i))
					{
						++acceptedUnlimited;
					}
				}

				var disabledAccept = service.Offload("Disabled", (_, _, _)
					=> Task.CompletedTask, 0);

				Assert.True(acceptedLimited);
				Assert.False(rejectedLimited);
				Assert.Equal(10, acceptedUnlimited);
				Assert.False(disabledAccept);

				Assert.Empty(logger.Entries);

				return Task.CompletedTask;
			});
		}

		[Fact]
		public Task ConcurrentOffloadRespectsBoundedCapacity()
		{
			const int capacity = 25;
			const int attempts = 100;
			const string category = "Concurrent";

			return RunWithOffloadServiceAsync(services =>
			{
				services.AddOffloadWorkService(opts =>
				{
					opts.AddCategory(category, new()
					{
						BoundedCapacity = capacity,
						MaxDegreeOfParallelism = capacity
					});
				});
				return CancellationToken.None;
			},
			async (service, logger) =>
			{
				var accepted = 0;
				var completed = 0;
				var tcs = new TaskCompletionSource();

				var tasks = Enumerable.Range(0, attempts).Select(_ => Task.Run(() =>
				{
					var result = service.Offload(category, async (_, _, _) =>
					{
						Interlocked.Increment(ref completed);
						await tcs.Task; // block all of them
					}, 0);

					if (result)
					{
						Interlocked.Increment(ref accepted);
					}
				}));

				await Task.WhenAll(tasks);
				Assert.Equal(capacity, service.GetActiveCount(category));
				Assert.Equal(capacity, accepted);
				Assert.True(accepted < attempts);
				
				tcs.SetResult(); // unblock all
				await Task.Delay(250); // allow completions to flush

				Assert.Equal(capacity, completed);
				Assert.Equal(0, service.GetActiveCount(category));
				Assert.Empty(logger.Entries);
			});
		}

		[Fact]
		public Task AddTwiceUsingDifferentOverloads()
		{
			return RunWithOffloadServiceAsync(services =>
			{
				services.AddOffloadWorkService(opts =>
				{
					opts.MaxDegreeOfParallelism = 2;
					opts.BoundedCapacity = 5;
				});

				var ex = Assert.Throws<InvalidOperationException>(() =>
				{
					services.AddOffloadWorkService((OffloadWorkServiceCategoryOptions cats) =>
					{
						cats.AddCategory("Another");
					});
				});

				Assert.Equal("AddOffloadWorkService has already been called.", ex.Message);
				return CancellationToken.None;
			},
			(_, _) => Task.CompletedTask);
		}

		[Fact]
		public async Task StressTest_MixedCategoryCapacities()
		{
			const int acceptedPerCat = 10;
			const int attemptsPerCat = 25;

			var categories = new[]
			{
				("Fast", new OffloadWorkServiceOptions { BoundedCapacity = acceptedPerCat, MaxDegreeOfParallelism = 5 }),
				("Slow", new OffloadWorkServiceOptions { BoundedCapacity = acceptedPerCat, MaxDegreeOfParallelism = 1 }),
				("Unlimited", new OffloadWorkServiceOptions { BoundedCapacity = -1, MaxDegreeOfParallelism = 10 }),
				("Disabled", new OffloadWorkServiceOptions { BoundedCapacity = 0, MaxDegreeOfParallelism = 1 }),
			};

			await RunWithOffloadServiceAsync(services =>
			{
				services.AddOffloadWorkService(opts =>
				{
					foreach (var (name, config) in categories)
						opts.AddCategory(name, config);
				});
				return CancellationToken.None;
			},
			async (service, logger) =>
			{
				var acceptedCounts = new ConcurrentDictionary<string, int>();
				var executionCounts = new ConcurrentDictionary<string, int>();
				var blocker = new TaskCompletionSource();
				var allTasks = new List<Task>();

				foreach (var (category, _) in categories)
				{
					for (int i = 0; i < attemptsPerCat; i++)
					{
						allTasks.Add(Task.Run(() =>
						{
							var accepted = service.Offload(category, async (_, _, _) =>
							{
								executionCounts.AddOrUpdate(category, 1, (_, count) => count + 1);
								await blocker.Task;
							}, 0);

							if (accepted)
							{
								acceptedCounts.AddOrUpdate(category, 1, (_, count) => count + 1);
							}
							else
							{
								acceptedCounts.AddOrUpdate($"{category}-Rejected", 1, (_, count) => count + 1);
							}
						}));
					}
				}

				await Task.WhenAll(allTasks);

				// validate accepted counts (before execution completes)
				Assert.Equal(acceptedPerCat, acceptedCounts["Fast"]);
				Assert.Equal(acceptedPerCat, acceptedCounts["Slow"]);
				Assert.Equal(attemptsPerCat, acceptedCounts["Unlimited"]);
				Assert.Equal(0, acceptedCounts.GetValueOrDefault("Disabled", 0));
				Assert.Equal(attemptsPerCat, acceptedCounts.GetValueOrDefault("Disabled-Rejected", 0));

				// validate active counts
				Assert.Equal(acceptedPerCat, service.GetActiveCount("Fast"));
				Assert.Equal(acceptedPerCat, service.GetActiveCount("Slow"));
				Assert.Equal(attemptsPerCat, service.GetActiveCount("Unlimited"));
				Assert.Equal(0, service.GetActiveCount("Disabled"));

				// unblock and flush
				blocker.SetResult();
				await Task.Delay(250);

				// ensure execution matches acceptance
				Assert.Equal(acceptedCounts["Fast"], executionCounts["Fast"]);
				Assert.Equal(acceptedCounts["Slow"], executionCounts["Slow"]);
				Assert.Equal(acceptedCounts["Unlimited"], executionCounts["Unlimited"]);
				Assert.Equal(0, executionCounts.GetValueOrDefault("Disabled", 0));

				// final active count checks
				Assert.Equal(0, service.GetActiveCount("Fast"));
				Assert.Equal(0, service.GetActiveCount("Slow"));
				Assert.Equal(0, service.GetActiveCount("Unlimited"));
				Assert.Equal(0, service.GetActiveCount("Disabled"));

				Assert.Empty(logger.Entries);
			});
		}

		[Fact]
		public Task CustomExceptionLogger_Is_Invoked()
		{
			return RunWithOffloadServiceAsync(services =>
			{
				services.AddOffloadWorkService();
				services.AddSingleton<IOffloadWorkExceptionLogger, CustomExceptionLogger>();
				return CancellationToken.None;
			},
			async (service, logger) =>
			{
				CustomExceptionLogger.Reset();

				service.Offload((_, _, _) => throw new InvalidProgramException("dead"), 0);
				await Task.Delay(250);

				Assert.Single(CustomExceptionLogger.Entries);
				Assert.Equal("dead", CustomExceptionLogger.Entries[0].Exception.Message);
				Assert.Null(CustomExceptionLogger.Entries[0].CategoryName);
				Assert.IsType<InvalidProgramException>(CustomExceptionLogger.Entries[0].Exception);

				Assert.Empty(logger.Entries);
			});
		}

		[Fact]
		public Task CustomExceptionLogger_Is_Invoked_WithCategory()
		{
			const string categoryName = "TestCategory";

			return RunWithOffloadServiceAsync(services =>
			{
				services.AddOffloadWorkService((OffloadWorkServiceCategoryOptions b) =>
				{
					b.AddCategory(categoryName);
				});
				services.AddSingleton<IOffloadWorkExceptionLogger, CustomExceptionLogger>();
				return CancellationToken.None;
			},
			async (service, logger) =>
			{
				CustomExceptionLogger.Reset();

				service.Offload(categoryName,
					(_, _, _) => throw new InvalidProgramException("dead category"), 0);

				await Task.Delay(250);

				Assert.Single(CustomExceptionLogger.Entries);
				var entry = CustomExceptionLogger.Entries[0];
				Assert.Equal("dead category", entry.Exception.Message);
				Assert.Equal(categoryName, entry.CategoryName);
				Assert.IsType<InvalidProgramException>(entry.Exception);

				Assert.Empty(logger.Entries);
			});
		}

	}
}
