// SPDX-License-Identifier: MIT
pragma solidity ^0.8.28;

import {IbcRelay} from "./IbcRelay.sol";

/// @title CrossChainMessage
/// @notice High-level cross-chain message passing abstraction over IBC.
/// Provides a user-friendly interface for sending and receiving messages
/// across chains, with automatic packet construction and event emission.
contract CrossChainMessage {
    /// @notice Message structure
    struct Message {
        address sender;
        address receiver;
        bytes payload;
        uint64 sequence;
        uint256 sourceChainId;
        uint256 destChainId;
        uint256 timeoutTimestamp;
    }

    /// @notice IbcRelay contract
    IbcRelay public ibcRelay;

    /// @notice Registered message handlers: (portId, channelId, handler address)
    mapping(bytes32 channelKey => address handler) public messageHandlers;

    /// @notice Sent message events for off-chain relayers to pick up
    event MessageSent(
        uint64 indexed sequence,
        bytes32 indexed channelKey,
        address indexed sender,
        address receiver,
        bytes payload,
        uint256 timeoutTimestamp
    );

    event MessageReceived(
        uint64 indexed sequence,
        bytes32 indexed channelKey,
        address indexed receiver,
        address sender,
        bytes payload
    );

    event HandlerRegistered(bytes32 indexed channelKey, address indexed handler);
    event HandlerRemoved(bytes32 indexed channelKey);

    error NotHandler();
    error ChannelNotOpen(bytes32 channelKey);
    error MessageTooLarge(uint256 size, uint256 max);

    uint256 public constant MAX_MESSAGE_SIZE = 1024 * 10; // 10KB

    constructor(address _ibcRelay) {
        ibcRelay = IbcRelay(_ibcRelay);
    }

    /// @notice Register a message handler for a (port, channel) pair.
    function registerHandler(
        string calldata portId,
        bytes32 channelId,
        address handler
    ) external {
        bytes32 key = keccak256(abi.encodePacked(portId, channelId));
        messageHandlers[key] = handler;
        emit HandlerRegistered(key, handler);
    }

    /// @notice Remove a message handler.
    function removeHandler(string calldata portId, bytes32 channelId) external {
        bytes32 key = keccak256(abi.encodePacked(portId, channelId));
        delete messageHandlers[key];
        emit HandlerRemoved(key);
    }

    /// @notice Send a cross-chain message.
    /// Constructs an IBC packet and emits a MessageSent event for relayers.
    function sendMessage(
        string calldata sourcePort,
        bytes32 sourceChannel,
        string calldata destPort,
        bytes32 destChannel,
        address receiver,
        bytes calldata payload,
        uint64 timeoutTimestamp
    ) external returns (uint64 sequence) {
        if (payload.length > MAX_MESSAGE_SIZE) {
            revert MessageTooLarge(payload.length, MAX_MESSAGE_SIZE);
        }

        // Encode the message as the IBC packet data
        bytes memory packetData = abi.encode(msg.sender, receiver, payload);

        sequence = ibcRelay.sendPacket(
            sourcePort,
            sourceChannel,
            destPort,
            destChannel,
            packetData,
            IbcRelay.Height({revisionNumber: 0, revisionHeight: 0}),
            timeoutTimestamp
        );

        bytes32 channelKey = keccak256(abi.encodePacked(sourcePort, sourceChannel));
        emit MessageSent(sequence, channelKey, msg.sender, receiver, payload, timeoutTimestamp);
    }

    /// @notice Handle an incoming message.
    /// Called by the relayer when a packet is received.
    function handleMessage(
        IbcRelay.Packet calldata packet,
        bytes calldata acknowledgement
    ) external {
        // Decode the message
        (address sender, address receiver, bytes memory payload) =
            abi.decode(packet.data, (address, address, bytes));

        bytes32 channelKey = keccak256(abi.encodePacked(packet.destPort, packet.destChannel));
        emit MessageReceived(packet.sequence, channelKey, receiver, sender, payload);
    }

    /// @notice Generate an acknowledgement for a received message.
    function generateAck(bool success, bytes memory data)
        external
        pure
        returns (bytes memory)
    {
        return abi.encode(success, data);
    }

    /// @notice Decode an acknowledgement.
    function decodeAck(bytes calldata acknowledgement)
        external
        pure
        returns (bool success, bytes memory data)
    {
        return abi.decode(acknowledgement, (bool, bytes));
    }

    /// @notice Get the channel key for a (port, channel) pair.
    function getChannelKey(string calldata portId, bytes32 channelId)
        external
        pure
        returns (bytes32)
    {
        return keccak256(abi.encodePacked(portId, channelId));
    }
}
