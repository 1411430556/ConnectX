﻿using ConnectX.Client.Route.Packet;
using Hive.Codec.Shared;
using MemoryPack;

namespace ConnectX.Client.Messages;

[MessageDefine]
[MemoryPackable]
public sealed partial class Pong : RouteLayerPacket
{
    public required uint SeqId { get; init; }

    [MemoryPackIgnore]
    public long SelfReceiveTime { get; set; }
}