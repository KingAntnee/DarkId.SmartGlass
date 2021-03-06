using System;
using System.Threading.Tasks;
using DarkId.SmartGlass.Common;
using DarkId.SmartGlass.Messaging;
using DarkId.SmartGlass.Messaging.Connection;
using DarkId.SmartGlass.Messaging.Session;
using DarkId.SmartGlass.Messaging.Session.Messages;
using DarkId.SmartGlass.Connection;
using DarkId.SmartGlass.Channels;

namespace DarkId.SmartGlass
{
    public class SmartGlassClient : IDisposable
    {
        private static readonly TimeSpan connectTimeout = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan[] connectRetries = new TimeSpan[]
        {
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(1500),
            TimeSpan.FromSeconds(5)
        };

        public static Task<SmartGlassClient> ConnectAsync(string addressOrHostname)
        {
            return ConnectAsync(addressOrHostname, null, null);
        }

        public static async Task<SmartGlassClient> ConnectAsync(
            string addressOrHostname, string xboxLiveUserHash, string xboxLiveAuthorization)
        {
            var device = await Device.PingAsync(addressOrHostname);
            var cryptoContext = new CryptoContext(device.Certificate);

            using (var transport = new MessageTransport(device.Address.ToString(), cryptoContext))
            {
                var deviceId = Guid.NewGuid();
                var sequenceNumber = 0u;

                var initVector = CryptoContext.GenerateRandomInitVector();

                Func<Task> connectFunc = async () =>
                {
                    var requestMessage = new ConnectRequestMessage();

                    requestMessage.InitVector = initVector;

                    requestMessage.DeviceId = deviceId;

                    requestMessage.UserHash = xboxLiveUserHash;
                    requestMessage.Authorization = xboxLiveAuthorization;

                    requestMessage.SequenceNumber = sequenceNumber;
                    requestMessage.SequenceBegin = sequenceNumber + 1;
                    requestMessage.SequenceEnd = sequenceNumber + 1;

                    sequenceNumber += 2;

                    await transport.SendAsync(requestMessage);
                };

                var response = await TaskExtensions.WithRetries(() =>
                    transport.WaitForMessageAsync<ConnectResponseMessage>(
                        connectTimeout,
                        () => connectFunc().Wait()),
                    connectRetries);

                return new SmartGlassClient(
                    device,
                    response,
                    cryptoContext);
            }
        }

        private readonly MessageTransport _messageTransport;
        private readonly SessionMessageTransport _sessionMessageTransport;

        private readonly DisposableAsyncLazy<InputChannel> _inputChannel;

        private uint _channelRequestId = 1;

        public event EventHandler<ConsoleStatusChangedEventArgs> ConsoleStatusChanged;

        private SmartGlassClient(
            Device device,
            ConnectResponseMessage connectResponse,
            CryptoContext cryptoContext)
        {
            _messageTransport = new MessageTransport(device.Address.ToString(), cryptoContext);
            _sessionMessageTransport = new SessionMessageTransport(
                _messageTransport,
                new SessionInfo()
                {
                    ParticipantId = connectResponse.ParticipantId
                });

            _sessionMessageTransport.MessageReceived += (s, e) =>
            {
                var consoleStatusMessage = e.Message as ConsoleStatusMessage;
                if (consoleStatusMessage != null)
                {
                    ConsoleStatusChanged?.Invoke(this, new ConsoleStatusChangedEventArgs(
                        new ConsoleStatus()
                        {
                            Configuration = consoleStatusMessage.Configuration,
                            ActiveTitles = consoleStatusMessage.ActiveTitles
                        }
                    ));
                }
            };

            _sessionMessageTransport.SendAsync(new LocalJoinMessage());

            _inputChannel = new DisposableAsyncLazy<InputChannel>(async () =>
            {
                return new InputChannel(await StartChannelAsync(ServiceType.SystemInput));
            });
        }

        public Task LaunchTitleAsync(
            uint titleId,
            string launchParams,
            ActiveTitleLocation location = ActiveTitleLocation.Default)
        {
            // TODO: Validate that Uri escape logic is correct. (Don't know of any valid existing title params.)

            return _sessionMessageTransport.SendAsync(new TitleLaunchMessage()
            {
                Uri = string.Format(
                    "ms-xbl-{0:X8}://default",
                    titleId,
                    string.IsNullOrWhiteSpace(launchParams) ?
                        string.Empty : "/" + Uri.EscapeDataString(launchParams)),
                Location = location
            });
        }

        public Task GameDvrRecord(int lastSeconds = 60)
        {
            return _sessionMessageTransport.SendAsync(new GameDvrRecordMessage()
            {
                StartTimeDelta = -lastSeconds,
            });
        }

        private async Task<ChannelMessageTransport> StartChannelAsync(ServiceType serviceType, uint titleId = 0)
        {
            var requestId = _channelRequestId++;

            // TODO: Formalize timeouts for response based messages.
            var response = await _sessionMessageTransport.WaitForMessageAsync<StartChannelResponseMessage>(
                TimeSpan.FromSeconds(1),
                () => _sessionMessageTransport.SendAsync(new StartChannelRequestMessage()
                {
                    ChannelRequestId = requestId,
                    ServiceType = serviceType,
                    TitleId = titleId
                }).Wait(),
                m => m.ChannelRequestId == requestId);

            if (response.Result != 0)
            {
                throw new SmartGlassException("Failed to open channel.", response.Result);
            }

            return new ChannelMessageTransport(response.ChannelId, _sessionMessageTransport);
        }

        // TODO: Show pairing state
        // TODO: Should the channel object be responsible for reestablishment when reconnection support is added?
        public Task<InputChannel> GetInputChannelAsync()
        {
            return _inputChannel.GetAsync();
        }

        public async Task<TitleChannel> StartTitleChannelAsync(uint titleId)
        {
            var channel = await StartChannelAsync(ServiceType.None, titleId);

            // TODO: See if this is an aux hello message that is only sent if available.
            // Currently waiting here as a convenience to prevent opening the stream before
            // this is received.

            try
            {
                await channel.WaitForMessageAsync<AuxiliaryStreamMessage>(TimeSpan.FromSeconds(1), () => {});
            }
            catch (TimeoutException)
            {
            }

            return new TitleChannel(channel);
        }

        public void Dispose()
        {
            // TODO: Close opened channels?
            // Assuming so for the time being, but don't know how to send stop messages yet
            _inputChannel.Dispose();

            _sessionMessageTransport.Dispose();
            _messageTransport.Dispose();
        }
    }
}