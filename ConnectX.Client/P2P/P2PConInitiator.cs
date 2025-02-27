﻿using System.Net;
using ConnectX.Client.P2P.LinkMaker;
using ConnectX.Shared.Messages;
using ConnectX.Shared.Messages.P2P;
using ConnectX.Shared.Models;
using Hive.Both.General.Dispatchers;
using Hive.Common.Shared.Helpers;
using Hive.Network.Abstractions.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZeroTier.Sockets;

namespace ConnectX.Client.P2P;

/// <summary>
///     A class that to help to establish P2P connection
/// </summary>
public class P2PConInitiator : IDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly IPEndPoint _localEndPoint;
    private readonly ILogger<P2PConInitiator> _logger;
    private readonly Guid _partnerId;
    private readonly P2PConContext _selfContext;
    private readonly IServiceProvider _serviceProvider;
    private readonly DispatchableSession _tmpLinkToServer;
    private TaskCompletionSource<ISession?>? _completionSource;

    public P2PConInitiator(
        IServiceProvider serviceProvider,
        Guid partnerId,
        DispatchableSession tmpLinkToServer,
        IPEndPoint localEndPoint,
        P2PConContext selfContext,
        ILogger<P2PConInitiator> logger)
    {
        _serviceProvider = serviceProvider;
        _partnerId = partnerId;
        _tmpLinkToServer = tmpLinkToServer;
        _localEndPoint = localEndPoint;
        _selfContext = selfContext;
        _logger = logger;
        _cancellationTokenSource = new CancellationTokenSource();

        tmpLinkToServer.Dispatcher.AddHandler<P2POpResult>(OnP2POpResultReceived);
        tmpLinkToServer.Dispatcher.AddHandler<P2PConReady>(OnP2PConReadyReceived);
    }

    public bool IsSucceeded { get; private set; }
    public bool IsConnecting { get; private set; } = true;
    public Socket? ZtServerSocket { get; private set; }
    public ISession? EstablishedConnection { get; private set; }
    public IPEndPoint? RemoteEndPoint { get; private set; }

    public void Dispose()
    {
        _tmpLinkToServer.Session.Close();
        _tmpLinkToServer?.Dispose();
        _cancellationTokenSource?.Dispose();
    }

    public async Task<ISession?> StartAsync()
    {
        _completionSource = new TaskCompletionSource<ISession?>();

        switch (_selfContext)
        {
            case P2PConRequest req:
                _logger.LogSendingP2PConRequest(_partnerId);
                await _tmpLinkToServer.Dispatcher.SendAsync(_tmpLinkToServer.Session, req);
                break;
            case P2PConAccept accept:
                _logger.LogSendingP2PConAccept(_partnerId);
                await _tmpLinkToServer.Dispatcher.SendAsync(_tmpLinkToServer.Session, accept);
                break;
            default:
                throw new InvalidOperationException("Invalid P2PConContext type");
        }

        _logger.LogStartedToEstablishP2PConnectionWithPartnerId(_partnerId);

        return await _completionSource.Task;
    }

    private async Task<ISession?> CreateDirectLinkToPartnerAsync(
        P2PLinkMaker linkMaker,
        DispatchableSession? serverTmpSocket = null)
    {
        var directLink = await linkMaker.BuildLinkAsync();

        if (serverTmpSocket != null)
        {
            _logger.LogClosedTempConnectionWithServer();

            await serverTmpSocket.Dispatcher.SendAsync(serverTmpSocket.Session, new ShutdownMessage());
            await Task.Delay(500);

            serverTmpSocket.Session.Close();
            serverTmpSocket.Dispose();
        }

        return directLink;
    }

    private void OnP2POpResultReceived(MessageContext<P2POpResult> ctx)
    {
        var message = ctx.Message;

        if (!message.IsSucceeded)
        {
            _logger.LogFailedToEstablishP2PConnectionWithPartnerId(_partnerId, message.ErrorMessage ?? "-");

            return;
        }

        if (message.Context == null) return;

        switch (message.Context)
        {
            case P2PConReady ready:
                P2PConReadyReceived(ready);
                break;
            default:
                _logger.LogReceivedP2POpResultButContextTypeNotBeenProcessed(_partnerId, message.Context?.GetType());
                break;
        }
    }

    private void OnP2PConReadyReceived(MessageContext<P2PConReady> ctx)
    {
        P2PConReadyReceived(ctx.Message);
    }

    private void P2PConReadyReceived(P2PConReady message)
    {
        _logger.LogReceivedP2PConReady(_partnerId);

        if (message.RecipientId != _partnerId)
        {
            _logger.LogP2PConReadyMismatch(_partnerId, message.RecipientId);

            return;
        }

        var connectionMaker = CreateLinkMaker(
            _partnerId,
            message.Time,
            message,
            _selfContext,
            _cancellationTokenSource.Token);

        Task.Run(async () =>
        {
            try
            {
                _logger.LogTryingToMakeConnection(_partnerId, _tmpLinkToServer.Session.LocalEndPoint,
                    message.PublicAddress, message.PublicPort);

                EstablishedConnection = await CreateDirectLinkToPartnerAsync(connectionMaker, _tmpLinkToServer);
                RemoteEndPoint = EstablishedConnection?.RemoteEndPoint;

                IsSucceeded = EstablishedConnection != null;
                IsConnecting = false;

                ZtServerSocket = connectionMaker.ZtServerSocket;

                _logger.LogP2PConnInfo(RemoteEndPoint!);

                _completionSource!.TrySetResult(EstablishedConnection);
            }
            catch (Exception e)
            {
                _completionSource!.SetException(e);
            }
        }, _cancellationTokenSource.Token).CatchException();
    }

    private ZtTcpSinglePortLinkMaker CreateLinkMaker(
        Guid partnerId,
        long time,
        P2PConContext targetContext,
        P2PConContext selfContext,
        CancellationToken token)
    {
        _logger.LogCreateP2PLinkMaker(selfContext);
        _logger.LogBothSidesCanConfirmTheirPortsUsingSinglePortLinkMaker();

        var remoteIpe = new IPEndPoint(targetContext.PublicAddress, targetContext.PublicPort);

        var linkMaker = ActivatorUtilities.CreateInstance<ZtTcpSinglePortLinkMaker>(
            _serviceProvider,
            selfContext is P2PConRequest,
            time,
            partnerId,
            selfContext.PublicAddress,
            selfContext.PublicPort,
            remoteIpe,
            token);

        return linkMaker;
    }
}

internal static partial class P2PConInitiatorLoggers
{
    [LoggerMessage(LogLevel.Information, "[P2P_CONN_INIT] P2P connection info: [Remote EndPoint [{Remote}]]")]
    public static partial void LogP2PConnInfo(this ILogger logger, IPEndPoint remote);

    [LoggerMessage(LogLevel.Information, "[P2P_CONN_INIT] Sending P2PConRequest to {PartnerId}")]
    public static partial void LogSendingP2PConRequest(this ILogger logger, Guid partnerId);

    [LoggerMessage(LogLevel.Information, "[P2P_CONN_INIT] Sending P2PConAccept to {PartnerId}")]
    public static partial void LogSendingP2PConAccept(this ILogger logger, Guid partnerId);

    [LoggerMessage(LogLevel.Information, "[P2P_CONN_INIT] Started to establish P2P connection with {PartnerId}")]
    public static partial void LogStartedToEstablishP2PConnectionWithPartnerId(this ILogger logger, Guid partnerId);

    [LoggerMessage(LogLevel.Information, "[P2P_CONN_INIT] Closed the temp connection with server")]
    public static partial void LogClosedTempConnectionWithServer(this ILogger logger);

    [LoggerMessage(LogLevel.Error,
        "[P2P_CONN_INIT] Failed to establish P2P connection with {PartnerId}, reason: {Reason}")]
    public static partial void LogFailedToEstablishP2PConnectionWithPartnerId(this ILogger logger, Guid partnerId,
        string reason);

    [LoggerMessage(LogLevel.Warning,
        "[P2P_CONN_INIT] Received P2POpResult from {PartnerId}, but the context type is not been processed, type: {Type}")]
    public static partial void LogReceivedP2POpResultButContextTypeNotBeenProcessed(this ILogger logger, Guid partnerId,
        Type? type);

    [LoggerMessage(LogLevel.Information, "[P2P_CONN_INIT] Received P2PConReady from {PartnerId}")]
    public static partial void LogReceivedP2PConReady(this ILogger logger, Guid partnerId);

    [LoggerMessage(LogLevel.Warning,
        "[P2P_CONN_INIT] Received P2PConReady from {PartnerId}, but the recipient is {RecipientId}")]
    public static partial void LogP2PConReadyMismatch(this ILogger logger, Guid partnerId, Guid recipientId);

    [LoggerMessage(LogLevel.Information,
        "[P2P_CONN_INIT] Trying to create direct link to {partnerId}, local endpoint: {localEndPoint}, remote endpoint: {remoteAddr}:{remotePort}")]
    public static partial void LogTryingToMakeConnection(this ILogger logger, Guid partnerId, IPEndPoint? localEndPoint,
        IPAddress? remoteAddr, int remotePort);

    [LoggerMessage(LogLevel.Trace, "[P2P_CONN_INIT] Smallest predict port is {Min}")]
    public static partial void LogSmallestPredictPort(this ILogger logger, int min);

    [LoggerMessage(LogLevel.Trace, "[P2P_CONN_INIT] Biggest predict port is {Max}")]
    public static partial void LogBiggestPredictPort(this ILogger logger, int max);

    [LoggerMessage(LogLevel.Trace, "[P2P_CONN_INIT] Predict port count is {Count}")]
    public static partial void LogPredictPortCount(this ILogger logger, int count);

    [LoggerMessage(LogLevel.Debug, "[P2P_CONN_INIT] Create P2PLinkMaker, {Context}")]
    public static partial void LogCreateP2PLinkMaker(this ILogger logger, P2PConContext context);

    [LoggerMessage(LogLevel.Information,
        "[P2P_CONN_INIT] Both sides can confirm their ports, using single port link maker")]
    public static partial void LogBothSidesCanConfirmTheirPortsUsingSinglePortLinkMaker(this ILogger logger);

    [LoggerMessage(LogLevel.Information,
        "[P2P_CONN_INIT] Only target can confirm its port, using many to single port link maker")]
    public static partial void LogOnlyTargetCanConfirmItsPortUsingManyToSinglePortLinkMaker(this ILogger logger);

    [LoggerMessage(LogLevel.Information,
        "[P2P_CONN_INIT] Only self can confirm its port, using single to many port link maker")]
    public static partial void LogOnlySelfCanConfirmItsPortUsingSingleToManyPortLinkMaker(this ILogger logger);

    [LoggerMessage(LogLevel.Information,
        "[P2P_CONN_INIT] Both sides cannot confirm their ports, using many to many port link maker")]
    public static partial void LogBothSidesCannotConfirmTheirPortsUsingManyToManyPortLinkMaker(this ILogger logger);
}