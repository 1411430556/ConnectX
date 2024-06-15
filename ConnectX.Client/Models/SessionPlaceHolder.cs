﻿using System.Net;
using Hive.Network.Abstractions;
using Hive.Network.Abstractions.Session;

namespace ConnectX.Client.Models;

public class SessionPlaceHolder : ISession
{
    public static readonly SessionPlaceHolder Shared = new();

    public SessionId Id => new();
    public IPEndPoint LocalEndPoint => new(0, 0);
    public IPEndPoint RemoteEndPoint => new(0, 0);
    public long LastHeartBeatTime => throw new NotImplementedException();
    public event SessionReceivedHandler? OnMessageReceived;

    public Task StartAsync(CancellationToken token)
    {
        throw new NotImplementedException();
    }

    public ValueTask SendAsync(MemoryStream ms, CancellationToken token = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask<bool> TrySendAsync(MemoryStream ms, CancellationToken token = default)
    {
        throw new NotImplementedException();
    }

    public void Close()
    {
        throw new NotImplementedException();
    }
}