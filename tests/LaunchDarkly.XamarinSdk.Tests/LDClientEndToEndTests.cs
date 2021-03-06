﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Common.Logging;
using LaunchDarkly.Client;
using LaunchDarkly.Xamarin.PlatformSpecific;
using LaunchDarkly.Xamarin.Tests.HttpHelpers;
using Xunit;
using Xunit.Abstractions;

namespace LaunchDarkly.Xamarin.Tests
{
    // Tests of an LDClient instance doing actual HTTP against an embedded server. These aren't intended to cover
    // every possible type of interaction, since the lower-level component tests like FeatureFlagRequestorTests
    // (and the DefaultEventProcessor and StreamManager tests in LaunchDarkly.CommonSdk) cover those more thoroughly.
    // These are more of a smoke test to ensure that the SDK is initializing and using those components in the
    // expected ways.
    public class LdClientEndToEndTests : BaseTest
    {
        private const string _mobileKey = "FAKE_KEY";

        private static readonly User _user = User.WithKey("foo");
        private static readonly User _otherUser = User.WithKey("bar");

        private static readonly IDictionary<string, string> _flagData1 = new Dictionary<string, string>
        {
            { "flag1", "value1" }
        };

        private static readonly IDictionary<string, string> _flagData2 = new Dictionary<string, string>
        {
            { "flag1", "value2" }
        };

        public static readonly IEnumerable<object[]> PollingAndStreaming = new List<object[]>
        {
            { new object[] { UpdateMode.Polling } },
            { new object[] { UpdateMode.Streaming } }
        };

        public LdClientEndToEndTests(ITestOutputHelper testOutput) : base(testOutput) { }

        [Theory]
        [MemberData(nameof(PollingAndStreaming))]
        public void InitGetsFlagsSync(UpdateMode mode)
        {
            using (var server = TestHttpServer.Start(SetupResponse(_flagData1, mode)))
            {
                var config = BaseConfig(server.Uri, mode);
                using (var client = TestUtil.CreateClient(config, _user, TimeSpan.FromSeconds(10)))
                {
                    VerifyRequest(server.Recorder, mode);
                    VerifyFlagValues(client, _flagData1);
                }
            }
        }

        [Theory]
        [MemberData(nameof(PollingAndStreaming))]
        public async Task InitGetsFlagsAsync(UpdateMode mode)
        {
            using (var server = TestHttpServer.Start(SetupResponse(_flagData1, mode)))
            {
                var config = BaseConfig(server.Uri, mode);
                using (var client = await TestUtil.CreateClientAsync(config, _user))
                {
                    VerifyRequest(server.Recorder, mode);
                }
            }
        }

        [Fact]
        public void StreamingInitMakesPollRequestIfStreamSendsPing()
        {
            Handler streamHandler = async ctx =>
            {
                ctx.AddHeader("Content-Type", "text/event-stream");
                await ctx.WriteChunkedDataAsync(Encoding.UTF8.GetBytes("event: ping\ndata: \n\n"));
                await Task.Delay(-1, ctx.CancellationToken);
            };
            using (var streamServer = TestHttpServer.Start(streamHandler))
            {
                using (var pollServer = TestHttpServer.Start(SetupResponse(_flagData1, UpdateMode.Polling)))
                {
                    var config = BaseConfig(streamServer.Uri, UpdateMode.Streaming,
                        b => b.BaseUri(pollServer.Uri));
                    using (var client = TestUtil.CreateClient(config, _user, TimeSpan.FromSeconds(5)))
                    {
                        VerifyRequest(streamServer.Recorder, UpdateMode.Streaming);
                        VerifyRequest(pollServer.Recorder, UpdateMode.Polling);
                        VerifyFlagValues(client, _flagData1);
                    }
                }
            }
        }

        [Fact]
        public void InitCanTimeOutSync()
        {
            var handler = Handlers.DelayBefore(TimeSpan.FromSeconds(2), SetupResponse(_flagData1, UpdateMode.Polling));
            using (var server = TestHttpServer.Start(handler))
            {
                using (var log = new LogSinkScope())
                {
                    var config = BaseConfig(server.Uri, builder => builder.IsStreamingEnabled(false));
                    using (var client = TestUtil.CreateClient(config, _user, TimeSpan.FromMilliseconds(200)))
                    {
                        Assert.False(client.Initialized);
                        Assert.Null(client.StringVariation(_flagData1.First().Key, null));
                        Assert.Contains(log.Messages, m => m.Level == LogLevel.Warn &&
                            m.Text == "Client did not successfully initialize within 200 milliseconds.");
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(PollingAndStreaming))]
        public void InitFailsOn401Sync(UpdateMode mode)
        {
            using (var server = TestHttpServer.Start(Handlers.Status(401)))
            {
                using (var log = new LogSinkScope())
                {
                    var config = BaseConfig(server.Uri, mode);
                    using (var client = TestUtil.CreateClient(config, _user, TimeSpan.FromSeconds(10)))
                    {
                        Assert.False(client.Initialized);
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(PollingAndStreaming))]
        public async Task InitFailsOn401Async(UpdateMode mode)
        {
            using (var server = TestHttpServer.Start(Handlers.Status(401)))
            {
                using (var log = new LogSinkScope())
                {
                    var config = BaseConfig(server.Uri, mode);

                    // Currently the behavior of LdClient.InitAsync is somewhat inconsistent with LdClient.Init if there is
                    // an unrecoverable error: LdClient.Init throws an exception, but LdClient.InitAsync returns a task that
                    // will complete successfully with an uninitialized client.
                    using (var client = await TestUtil.CreateClientAsync(config, _user))
                    {
                        Assert.False(client.Initialized);
                    }
                }
            }
        }

        [Fact]
        public async Task InitWithKeylessAnonUserAddsKeyAndReusesIt()
        {
            // Note, we don't care about polling mode vs. streaming mode for this functionality.
            using (var server = TestHttpServer.Start(SetupResponse(_flagData1, UpdateMode.Polling)))
            {
                var config = BaseConfig(server.Uri, UpdateMode.Polling);
                var name = "Sue";
                var anonUser = User.Builder((string)null).Name(name).Anonymous(true).Build();

                // Note, on mobile platforms, the generated user key is the device ID and is stable; on other platforms,
                // it's a GUID that is cached in local storage. Calling ClearCachedClientId() resets the latter.
                ClientIdentifier.ClearCachedClientId();

                string generatedKey = null;
                using (var client = await TestUtil.CreateClientAsync(config, anonUser))
                {
                    Assert.NotNull(client.User.Key);
                    generatedKey = client.User.Key;
                    Assert.Equal(name, client.User.Name);
                }

                using (var client = await TestUtil.CreateClientAsync(config, anonUser))
                {
                    Assert.Equal(generatedKey, client.User.Key);
                }
            }
        }

        [Theory]
        [MemberData(nameof(PollingAndStreaming))]
        public void IdentifySwitchesUserAndGetsFlagsSync(UpdateMode mode)
        {
            var switchable = Handlers.DelegateTo(SetupResponse(_flagData1, mode));
            using (var server = TestHttpServer.Start(switchable))
            {
                var config = BaseConfig(server.Uri, mode);
                using (var client = TestUtil.CreateClient(config, _user))
                {
                    var req1 = VerifyRequest(server.Recorder, mode);
                    VerifyFlagValues(client, _flagData1);
                    var user1RequestPath = req1.Path;

                    switchable.Target = SetupResponse(_flagData2, mode);

                    var success = client.Identify(_otherUser, TimeSpan.FromSeconds(5));
                    Assert.True(success);
                    Assert.True(client.Initialized);
                    Assert.Equal(_otherUser.Key, client.User.Key); // don't compare entire user, because SDK may have added device/os attributes

                    var req2 = VerifyRequest(server.Recorder, mode);
                    Assert.NotEqual(user1RequestPath, req2.Path);
                    VerifyFlagValues(client, _flagData2);
                }
            }
        }

        [Theory]
        [MemberData(nameof(PollingAndStreaming))]
        public async Task IdentifySwitchesUserAndGetsFlagsAsync(UpdateMode mode)
        {
            var switchable = Handlers.DelegateTo(SetupResponse(_flagData1, mode));
            using (var server = TestHttpServer.Start(switchable))
            {
                var config = BaseConfig(server.Uri, mode);
                using (var client = await TestUtil.CreateClientAsync(config, _user))
                {
                    var req1 = VerifyRequest(server.Recorder, mode);
                    VerifyFlagValues(client, _flagData1);
                    var user1RequestPath = req1.Path;

                    switchable.Target = SetupResponse(_flagData2, mode);

                    var success = await client.IdentifyAsync(_otherUser);
                    Assert.True(success);
                    Assert.True(client.Initialized);
                    Assert.Equal(_otherUser.Key, client.User.Key); // don't compare entire user, because SDK may have added device/os attributes

                    var req2 = VerifyRequest(server.Recorder, mode);
                    Assert.NotEqual(user1RequestPath, req2.Path);
                    VerifyFlagValues(client, _flagData2);
                }
            }
        }

        [Theory]
        [MemberData(nameof(PollingAndStreaming))]
        public void IdentifyCanTimeOutSync(UpdateMode mode)
        {
            var switchable = Handlers.DelegateTo(SetupResponse(_flagData1, mode));
            using (var server = TestHttpServer.Start(switchable))
            {
                var config = BaseConfig(server.Uri, mode);
                using (var client = TestUtil.CreateClient(config, _user))
                {
                    var req1 = VerifyRequest(server.Recorder, mode);
                    VerifyFlagValues(client, _flagData1);

                    switchable.Target = Handlers.DelayBefore(TimeSpan.FromSeconds(2),
                        SetupResponse(_flagData1, mode));

                    var success = client.Identify(_otherUser, TimeSpan.FromMilliseconds(100));
                    Assert.False(success);
                    Assert.False(client.Initialized);
                    Assert.Null(client.StringVariation(_flagData1.First().Key, null));
                }
            }
        }

        [Theory]
        [InlineData("", "/mobile/events/bulk")]
        [InlineData("/basepath", "/basepath/mobile/events/bulk")]
        [InlineData("/basepath/", "/basepath/mobile/events/bulk")]
        public void EventsAreSentToCorrectEndpointAsync(
            string baseUriExtraPath,
            string expectedPath
            )
        {
            using (var server = TestHttpServer.Start(Handlers.Status(202)))
            {
                var config = Configuration.BuilderInternal(_mobileKey)
                    .UpdateProcessorFactory(MockPollingProcessor.Factory("{}"))
                    .EventsUri(new Uri(server.Uri.ToString() + baseUriExtraPath))
                    .PersistFlagValues(false)
                    .Build();

                using (var client = TestUtil.CreateClient(config, _user))
                {
                    client.Flush();
                    var req = server.Recorder.RequireRequest(TimeSpan.FromSeconds(5));

                    Assert.Equal("POST", req.Method);
                    Assert.Equal(expectedPath, req.Path);
                    Assert.Equal(LdValueType.Array, LdValue.Parse(req.Body).Type);
                }
            }
        }

        [Fact]
        public void OfflineClientUsesCachedFlagsSync()
        {
            // streaming vs. polling should make no difference for this
            using (var server = TestHttpServer.Start(SetupResponse(_flagData1, UpdateMode.Polling)))
            {
                ClearCachedFlags(_user);
                try
                {
                    var config = BaseConfig(server.Uri, UpdateMode.Polling, builder => builder.PersistFlagValues(true));
                    using (var client = TestUtil.CreateClient(config, _user))
                    {
                        VerifyFlagValues(client, _flagData1);
                    }

                    // At this point the SDK should have written the flags to persistent storage for this user key.
                    // We'll now start over in offline mode, and we should still see the earlier flag values.
                    var offlineConfig = Configuration.Builder(_mobileKey).Offline(true).Build();
                    using (var client = TestUtil.CreateClient(offlineConfig, _user))
                    {
                        VerifyFlagValues(client, _flagData1);
                    }
                }
                finally
                {
                    ClearCachedFlags(_user);
                }
            }
        }

        [Fact]
        public async Task OfflineClientUsesCachedFlagsAsync()
        {
            // streaming vs. polling should make no difference for this
            using (var server = TestHttpServer.Start(SetupResponse(_flagData1, UpdateMode.Polling)))
            {
                ClearCachedFlags(_user);
                try
                {
                    var config = BaseConfig(server.Uri, UpdateMode.Polling, builder => builder.PersistFlagValues(true));
                    using (var client = await TestUtil.CreateClientAsync(config, _user))
                    {
                        VerifyFlagValues(client, _flagData1);
                    }

                    // At this point the SDK should have written the flags to persistent storage for this user key.
                    var offlineConfig = Configuration.Builder(_mobileKey).Offline(true).Build();
                    using (var client = await TestUtil.CreateClientAsync(offlineConfig, _user))
                    {
                        VerifyFlagValues(client, _flagData1);
                    }
                }
                finally
                {
                    ClearCachedFlags(_user);
                }
            }
        }

        [Fact]
        public async Task BackgroundModeForcesPollingAsync()
        {
            var mockBackgroundModeManager = new MockBackgroundModeManager();
            var backgroundInterval = TimeSpan.FromMilliseconds(50);

            ClearCachedFlags(_user);

            var switchable = Handlers.DelegateTo(SetupResponse(_flagData1, UpdateMode.Streaming));
            using (var server = TestHttpServer.Start(switchable))
            {
                var config = BaseConfig(server.Uri, UpdateMode.Streaming, builder => builder
                    .BackgroundModeManager(mockBackgroundModeManager)
                    .BackgroundPollingIntervalWithoutMinimum(backgroundInterval)
                    .PersistFlagValues(false));

                using (var client = await TestUtil.CreateClientAsync(config, _user))
                {
                    VerifyFlagValues(client, _flagData1);

                    // Set it up so that when the client switches to background mode and does a polling request, it will
                    // receive _flagData2, and we will be notified of that via a change event. SetupResponse will only
                    // configure the polling endpoint, so if the client makes a streaming request here it'll fail.
                    switchable.Target = SetupResponse(_flagData2, UpdateMode.Polling);
                    var receivedChangeSignal = new SemaphoreSlim(0, 1);
                    client.FlagChanged += (sender, args) =>
                    {
                        receivedChangeSignal.Release();
                    };

                    mockBackgroundModeManager.UpdateBackgroundMode(true);

                    await receivedChangeSignal.WaitAsync();
                    VerifyFlagValues(client, _flagData2);

                    // Now switch back to streaming
                    switchable.Target = SetupResponse(_flagData1, UpdateMode.Streaming);
                    mockBackgroundModeManager.UpdateBackgroundMode(false);

                    await receivedChangeSignal.WaitAsync();
                    VerifyFlagValues(client, _flagData1);
                }
            }
        }

        [Fact]
        public async Task BackgroundModePollingCanBeDisabledAsync()
        {
            var mockBackgroundModeManager = new MockBackgroundModeManager();
            var backgroundInterval = TimeSpan.FromMilliseconds(50);
            var hackyUpdateDelay = TimeSpan.FromMilliseconds(200);

            ClearCachedFlags(_user);
            var switchable = Handlers.DelegateTo(SetupResponse(_flagData1, UpdateMode.Streaming));
            using (var server = TestHttpServer.Start(switchable))
            {
                var config = BaseConfig(server.Uri, UpdateMode.Streaming, builder => builder
                    .BackgroundModeManager(mockBackgroundModeManager)
                    .EnableBackgroundUpdating(false)
                    .BackgroundPollingInterval(backgroundInterval)
                    .PersistFlagValues(false));

                using (var client = await TestUtil.CreateClientAsync(config, _user))
                {
                    VerifyFlagValues(client, _flagData1);

                    // The SDK should *not* hit this polling endpoint, but we're providing some data there so we can
                    // detect whether it does.
                    switchable.Target = SetupResponse(_flagData2, UpdateMode.Polling);
                    mockBackgroundModeManager.UpdateBackgroundMode(true);

                    await Task.Delay(hackyUpdateDelay);
                    VerifyFlagValues(client, _flagData1);  // we should *not* have done a poll

                    var receivedChangeSignal = new SemaphoreSlim(0, 1);
                    client.FlagChanged += (sender, args) =>
                    {
                        receivedChangeSignal.Release();
                    };

                    // Now switch back to streaming
                    switchable.Target = SetupResponse(_flagData2, UpdateMode.Streaming);
                    mockBackgroundModeManager.UpdateBackgroundMode(false);

                    await receivedChangeSignal.WaitAsync();
                    VerifyFlagValues(client, _flagData2);
                }
            }
        }

        [Theory]
        [MemberData(nameof(PollingAndStreaming))]
        public async Task OfflineClientGoesOnlineAndGetsFlagsAsync(UpdateMode mode)
        {
            using (var server = TestHttpServer.Start(SetupResponse(_flagData1, mode)))
            {
                ClearCachedFlags(_user);
                var config = BaseConfig(server.Uri, mode, builder => builder.Offline(true).PersistFlagValues(false));
                using (var client = await TestUtil.CreateClientAsync(config, _user))
                {
                    VerifyNoFlagValues(client, _flagData1);
                    Assert.Equal(0, server.Recorder.Count);

                    await client.SetOfflineAsync(false);

                    VerifyFlagValues(client, _flagData1);
                }
            }
        }

        [Theory]
        [MemberData(nameof(PollingAndStreaming))]
        public void DateLikeStringValueIsStillParsedAsString(UpdateMode mode)
        {
            // Newtonsoft.Json's default behavior is to transform ISO date/time strings into DateTime objects. We
            // definitely don't want that. Verify that we're disabling that behavior when we parse flags.
            const string dateLikeString1 = "1970-01-01T00:00:01.001Z";
            const string dateLikeString2 = "1970-01-01T00:00:01Z";
            var flagData = new Dictionary<string, string>
                {
                    { "flag1", dateLikeString1 },
                    { "flag2", dateLikeString2 }
                };
            using (var server = TestHttpServer.Start(SetupResponse(flagData, mode)))
            {
                var config = BaseConfig(server.Uri, mode);
                using (var client = TestUtil.CreateClient(config, _user))
                {
                    VerifyFlagValues(client, flagData);
                }
            }
        }

        private Configuration BaseConfig(Uri serverUri, Func<ConfigurationBuilder, IConfigurationBuilder> extraConfig = null)
        {
            var builderInternal = Configuration.BuilderInternal(_mobileKey)
                .EventProcessor(new MockEventProcessor());
            builderInternal
                .BaseUri(serverUri)
                .StreamUri(serverUri)
                .PersistFlagValues(false);  // unless we're specifically testing flag caching, this helps to prevent test state contamination
            var builder = extraConfig == null ? builderInternal : extraConfig(builderInternal);
            return builder.Build();
        }

        private Configuration BaseConfig(Uri serverUri, UpdateMode mode, Func<ConfigurationBuilder, IConfigurationBuilder> extraConfig = null)
        {
            return BaseConfig(serverUri, builder =>
            {
                builder.IsStreamingEnabled(mode.IsStreaming);
                return extraConfig == null ? builder : extraConfig(builder);
            });
        }

        private Handler SetupResponse(IDictionary<string, string> data, UpdateMode mode)
        {
            var body = mode.IsStreaming ? StreamingData(data) : PollingData(data);
            return async ctx =>
            {
                if (mode.IsStreaming)
                {
                    ctx.SetHeader("Content-Type", "text/event-stream");
                    var bytes = Encoding.UTF8.GetBytes(body);
                    await ctx.WriteChunkedDataAsync(bytes);
                    await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
                }
                else
                {
                    await Handlers.JsonResponse(body)(ctx);
                }
            };
        }

        private RequestInfo VerifyRequest(RequestRecorder recorder, UpdateMode mode)
        {
            var req = recorder.RequireRequest(TimeSpan.FromSeconds(5));
            Assert.Equal("GET", req.Method);

            // Note, we don't check for an exact match of the encoded user string in Req.Path because it is not determinate - the
            // SDK may add custom attributes to the user ("os" etc.) and since we don't canonicalize the JSON representation,
            // properties could be serialized in any order causing the encoding to vary. Also, we don't test REPORT mode here
            // because it is already covered in FeatureFlagRequestorTest.
            Assert.Matches(mode.FlagsPathRegex, req.Path);

            Assert.Equal("", req.Query);
            Assert.Equal(_mobileKey, req.Headers["Authorization"]);
            Assert.Null(req.Body);

            return req;
        }

        private void VerifyFlagValues(ILdClient client, IDictionary<string, string> flags)
        {
            Assert.True(client.Initialized);
            foreach (var e in flags)
            {
                Assert.Equal(e.Value, client.StringVariation(e.Key, null));
            }
        }

        private void VerifyNoFlagValues(ILdClient client, IDictionary<string, string> flags)
        {
            Assert.True(client.Initialized);
            foreach (var e in flags)
            {
                Assert.Null(client.StringVariation(e.Key, null));
            }
        }

        private static LdValue FlagJson(string key, string value)
        {
            return LdValue.ObjectFrom(new Dictionary<string, LdValue>
            {
                { "key", LdValue.Of(key) },
                { "value", LdValue.Of(value) }
            });
        }

        private static string PollingData(IDictionary<string, string> flags)
        {
            var d = new Dictionary<string, LdValue>();
            foreach (var e in flags)
            {
                d.Add(e.Key, FlagJson(e.Key, e.Value));
            }
            return LdValue.ObjectFrom(d).ToJsonString();
        }

        private static string StreamingData(IDictionary<string, string> flags)
        {
            return "event: put\ndata: " + PollingData(flags) + "\n\n";
        }
    }

    public class UpdateMode
    {
        public bool IsStreaming { get; private set; }
        public string FlagsPathRegex { get; private set; }

        public static readonly UpdateMode Streaming = new UpdateMode
        {
            IsStreaming = true,
            FlagsPathRegex = "^/meval/[^/?]+"
        };

        public static readonly UpdateMode Polling = new UpdateMode
        {
            IsStreaming = false,
            FlagsPathRegex = "^/msdk/evalx/users/[^/?]+"
        };

        public override string ToString() => IsStreaming ? "Streaming" : "Polling";
    }
}
