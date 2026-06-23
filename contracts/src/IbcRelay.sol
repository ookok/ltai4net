// SPDX-License-Identifier: MIT
pragma solidity ^0.8.28;

import {ZKPVerify} from "./ZKPVerify.sol";
import {LightClient} from "./LightClient.sol";

/// @title IbcRelay
/// @notice IBC (Inter-Blockchain Communication) packet relay contract.
/// Handles packet send, receive, acknowledgement, and timeout.
/// Uses LightClient + ZKPVerify for cross-chain state verification.
contract IbcRelay {
    error NotRelayer();
    error NotVerifier();
    error UnauthorizedPort();
    error ChannelAlreadyExists(bytes32 channelId);
    error ChannelNotFound(bytes32 channelId);
    error ConnectionNotFound(bytes32 connectionId);
    error PacketAlreadyReceived(uint64 sequence);
    error PacketTimedOut();
    error InvalidPacket();
    error InvalidProof();

    /// @notice IBC connection end
    struct ConnectionEnd {
        string clientId;
        string counterpartyClientId;
        bytes32 counterpartyConnectionId;
        uint8 state; // 0=INIT, 1=TRYOPEN, 2=OPEN
    }

    /// @notice IBC channel
    struct Channel {
        uint8 state; // 0=INIT, 1=TRYOPEN, 2=OPEN, 3=CLOSED
        uint8 ordering; // 0=UNORDERED, 1=ORDERED
        string counterpartyPortId;
        bytes32 counterpartyChannelId;
        string version;
        bytes32 connectionHops; // Single connection for simplicity
    }

    /// @notice IBC packet
    struct Packet {
        uint64 sequence;
        string sourcePort;
        bytes32 sourceChannel;
        string destPort;
        bytes32 destChannel;
        bytes data;
        Height timeoutHeight;
        uint64 timeoutTimestamp;
    }

    /// @notice Block height (revision number + revision height)
    struct Height {
        uint64 revisionNumber;
        uint64 revisionHeight;
    }

    /// @notice Verifier role (manages connections and channels)
    address public verifier;
    /// @notice Relayer role (submits packets)
    address public relayer;
    /// @notice LightClient contract
    LightClient public lightClient;

    /// @notice connectionId => ConnectionEnd
    mapping(bytes32 connectionId => ConnectionEnd) public connections;
    /// @notice (portId, channelId) => Channel
    mapping(bytes32 portChannelHash => Channel) public channels;
    /// @notice Sequence number tracking per (portId, channelId) for ordered channels
    mapping(bytes32 portChannelHash => uint64 nextSequenceSend) public nextSequenceSends;
    mapping(bytes32 portChannelHash => uint64 nextSequenceRecv) public nextSequenceRecvs;
    mapping(bytes32 portChannelHash => uint64 nextSequenceAck) public nextSequenceAcks;

    /// @notice Received packet commitments: hash(packet) => bool
    mapping(bytes32 packetCommitment => bool) public packetReceipts;

    /// @notice Allowed ports
    mapping(string portId => bool) public allowedPorts;

    event ConnectionOpened(bytes32 indexed connectionId, string clientId, string counterpartyClientId);
    event ChannelOpened(bytes32 indexed channelId, string portId, uint8 ordering);
    event ChannelClosed(bytes32 indexed channelId, string portId);
    event PacketSent(uint64 indexed sequence, string sourcePort, bytes32 sourceChannel, string destPort, bytes32 destChannel, bytes data);
    event PacketReceived(uint64 indexed sequence, string sourcePort, bytes32 sourceChannel, string destPort, bytes32 destChannel);
    event PacketAcknowledged(uint64 indexed sequence, string sourcePort, bytes32 sourceChannel);
    event PacketTimedOut(uint64 indexed sequence, string sourcePort, bytes32 sourceChannel);
    event VerifierUpdated(address indexed oldVerifier, address indexed newVerifier);
    event RelayerUpdated(address indexed oldRelayer, address indexed newRelayer);

    modifier onlyVerifier() {
        if (msg.sender != verifier) revert NotVerifier();
        _;
    }

    modifier onlyRelayer() {
        if (msg.sender != relayer) revert NotRelayer();
        _;
    }

    modifier onlyAllowedPort(string calldata portId) {
        if (!allowedPorts[portId]) revert UnauthorizedPort();
        _;
    }

    constructor(address _lightClient, address _verifier, address _relayer) {
        lightClient = LightClient(_lightClient);
        verifier = _verifier;
        relayer = _relayer;
    }

    /// @notice Set relayer address.
    function setRelayer(address newRelayer) external onlyVerifier {
        emit RelayerUpdated(relayer, newRelayer);
        relayer = newRelayer;
    }

    /// @notice Set verifier address.
    function setVerifier(address newVerifier) external onlyVerifier {
        emit VerifierUpdated(verifier, newVerifier);
        verifier = newVerifier;
    }

    /// @notice Allow a port ID.
    function allowPort(string calldata portId) external onlyVerifier {
        allowedPorts[portId] = true;
    }

    /// @notice Disallow a port ID.
    function disallowPort(string calldata portId) external onlyVerifier {
        allowedPorts[portId] = false;
    }

    /// @notice Open an IBC connection.
    function connectionOpenTry(
        bytes32 connectionId,
        string calldata clientId,
        string calldata counterpartyClientId,
        bytes32 counterpartyConnectionId
    ) external onlyVerifier {
        connections[connectionId] = ConnectionEnd({
            clientId: clientId,
            counterpartyClientId: counterpartyClientId,
            counterpartyConnectionId: counterpartyConnectionId,
            state: 2 // OPEN
        });
        emit ConnectionOpened(connectionId, clientId, counterpartyClientId);
    }

    /// @notice Open an IBC channel.
    function channelOpenTry(
        bytes32 channelId,
        string calldata portId,
        uint8 ordering,
        string calldata counterpartyPortId,
        bytes32 counterpartyChannelId,
        string calldata version,
        bytes32 connectionHops
    ) external onlyVerifier onlyAllowedPort(portId) {
        bytes32 key = keccak256(abi.encodePacked(portId, channelId));
        if (channels[key].state != 0) revert ChannelAlreadyExists(channelId);

        channels[key] = Channel({
            state: 2, // OPEN
            ordering: ordering,
            counterpartyPortId: counterpartyPortId,
            counterpartyChannelId: counterpartyChannelId,
            version: version,
            connectionHops: connectionHops
        });

        emit ChannelOpened(channelId, portId, ordering);
    }

    /// @notice Close an IBC channel.
    function channelClose(string calldata portId, bytes32 channelId) external onlyVerifier {
        bytes32 key = keccak256(abi.encodePacked(portId, channelId));
        if (channels[key].state == 0) revert ChannelNotFound(channelId);
        channels[key].state = 3; // CLOSED
        emit ChannelClosed(channelId, portId);
    }

    /// @notice Send an IBC packet.
    function sendPacket(
        string calldata sourcePort,
        bytes32 sourceChannel,
        string calldata destPort,
        bytes32 destChannel,
        bytes calldata data,
        Height calldata timeoutHeight,
        uint64 timeoutTimestamp
    ) external onlyAllowedPort(sourcePort) returns (uint64 sequence) {
        bytes32 key = keccak256(abi.encodePacked(sourcePort, sourceChannel));
        if (channels[key].state != 2) revert ChannelNotFound(sourceChannel);

        sequence = nextSequenceSends[key];
        nextSequenceSends[key] = sequence + 1;

        emit PacketSent(sequence, sourcePort, sourceChannel, destPort, destChannel, data);
    }

    /// @notice Receive an IBC packet with proof verification.
    function recvPacket(
        Packet calldata packet,
        ZKPVerify.Proof calldata proof,
        uint256[] calldata publicInputs,
        bytes32[] calldata merkleProof
    ) external onlyRelayer {
        bytes32 destKey = keccak256(abi.encodePacked(packet.destPort, packet.destChannel));
        Channel storage ch = channels[destKey];
        if (ch.state != 2) revert ChannelNotFound(packet.destChannel);

        bytes32 packetCommitment = hashPacket(packet);
        if (packetReceipts[packetCommitment]) {
            revert PacketAlreadyReceived(packet.sequence);
        }

        // Verify packet commitment on source chain via LightClient
        bytes32 commitmentPath = keccak256(
            abi.encodePacked(packet.sourcePort, packet.sourceChannel, packet.sequence)
        );
        bool membership = lightClient.verifyMembership(
            publicInputs[0], // height
            commitmentPath,
            packetCommitment,
            merkleProof
        );
        if (!membership) revert InvalidProof();

        packetReceipts[packetCommitment] = true;

        if (ch.ordering == 1) {
            // ORDERED: enforce sequence
            bytes32 seqKey = keccak256(abi.encodePacked(packet.destPort, packet.destChannel));
            if (packet.sequence != nextSequenceRecvs[seqKey]) revert InvalidPacket();
            nextSequenceRecvs[seqKey] = packet.sequence + 1;
        }

        emit PacketReceived(packet.sequence, packet.sourcePort, packet.sourceChannel,
            packet.destPort, packet.destChannel);
    }

    /// @notice Acknowledge a received packet.
    function acknowledgePacket(
        Packet calldata packet,
        bytes calldata acknowledgement,
        ZKPVerify.Proof calldata proof,
        uint256[] calldata publicInputs,
        bytes32[] calldata merkleProof
    ) external onlyRelayer {
        bytes32 sourceKey = keccak256(abi.encodePacked(packet.sourcePort, packet.sourceChannel));
        Channel storage ch = channels[sourceKey];
        if (ch.state != 2) revert ChannelNotFound(packet.sourceChannel);

        if (ch.ordering == 1) {
            bytes32 seqKey = sourceKey;
            if (packet.sequence != nextSequenceAcks[seqKey]) revert InvalidPacket();
            nextSequenceAcks[seqKey] = packet.sequence + 1;
        }

        // Verify acknowledgement commitment on counterparty
        bytes32 ackPath = keccak256(
            abi.encodePacked(packet.destPort, packet.destChannel, packet.sequence)
        );
        bytes32 ackCommitment = keccak256(acknowledgement);
        bool membership = lightClient.verifyMembership(
            publicInputs[0], // height
            ackPath,
            ackCommitment,
            merkleProof
        );
        if (!membership) revert InvalidProof();

        emit PacketAcknowledged(packet.sequence, packet.sourcePort, packet.sourceChannel);
    }

    /// @notice Handle packet timeout.
    function timeoutPacket(
        Packet calldata packet,
        ZKPVerify.Proof calldata proof,
        uint256[] calldata publicInputs,
        bytes32[] calldata merkleProof
    ) external onlyRelayer {
        // Verify that the packet was not received on the destination chain
        bytes32 recvPath = keccak256(
            abi.encodePacked(packet.destPort, packet.destChannel, packet.sequence)
        );
        bool nonMembership = lightClient.verifyNonMembership(
            publicInputs[0],
            recvPath,
            merkleProof
        );
        if (!nonMembership) revert InvalidProof();

        emit PacketTimedOut(packet.sequence, packet.sourcePort, packet.sourceChannel);
    }

    /// @dev Hash a packet for commitment.
    function hashPacket(Packet calldata packet) internal pure returns (bytes32) {
        return keccak256(abi.encode(
            packet.sequence,
            keccak256(bytes(packet.sourcePort)),
            packet.sourceChannel,
            keccak256(bytes(packet.destPort)),
            packet.destChannel,
            keccak256(packet.data),
            packet.timeoutHeight.revisionNumber,
            packet.timeoutHeight.revisionHeight,
            packet.timeoutTimestamp
        ));
    }
}
