// SPDX-License-Identifier: MIT
pragma solidity ^0.8.28;

import {IbcRelay} from "../IbcRelay.sol";

/// @title RelayerFee
/// @notice Fee management for IBC relayers.
/// Relayers pay gas to submit packets; this contract handles fee distribution.
contract RelayerFee {
    error NotRelayer();
    error InsufficientFee();
    error FeeTooLow(uint256 required, uint256 provided);
    error AlreadyClaimed(uint64 sequence, bytes32 channelId);

    /// @notice Fee tiers for different IBC operations
    enum FeeTier { RELAY, ACK, TIMEOUT }

    /// @notice Fee structure per operation type
    struct FeeSchedule {
        uint256 relayBase;     // Base fee for packet relay
        uint256 relayPerByte;  // Fee per byte of packet data
        uint256 ackBase;       // Base fee for acknowledgement
        uint256 timeoutBase;   // Base fee for timeout
    }

    /// @notice Fee claim record
    struct Claim {
        bool claimed;
        address claimant;
        uint256 amount;
    }

    /// @notice IbcRelay contract
    IbcRelay public ibcRelay;
    /// @notice Fee schedule
    FeeSchedule public feeSchedule;
    /// @notice Owner (can update fees)
    address public owner;

    /// @notice (channelId, sequence) => Claim
    mapping(bytes32 channelKey => mapping(uint64 sequence => Claim)) public relayClaims;
    mapping(bytes32 channelKey => mapping(uint64 sequence => Claim)) public ackClaims;
    mapping(bytes32 channelKey => mapping(uint64 sequence => Claim)) public timeoutClaims;

    /// @notice Accumulated fees available for withdrawal
    mapping(address relayer => uint256) public pendingFees;

    event FeeScheduleUpdated(FeeSchedule newSchedule);
    event FeePaid(address indexed relayer, bytes32 indexed channelId, uint64 sequence, FeeTier tier, uint256 amount);
    event FeeWithdrawn(address indexed relayer, uint256 amount);
    event OwnershipTransferred(address indexed previousOwner, address indexed newOwner);

    modifier onlyOwner() {
        if (msg.sender != owner) revert NotRelayer();
        _;
    }

    constructor(address _ibcRelay, address _owner) {
        ibcRelay = IbcRelay(_ibcRelay);
        owner = _owner;
        feeSchedule = FeeSchedule({
            relayBase: 0.001 ether,
            relayPerByte: 0.0001 ether,
            ackBase: 0.0005 ether,
            timeoutBase: 0.0005 ether
        });
    }

    /// @notice Transfer ownership.
    function transferOwnership(address newOwner) external onlyOwner {
        emit OwnershipTransferred(owner, newOwner);
        owner = newOwner;
    }

    /// @notice Update fee schedule.
    function updateFeeSchedule(FeeSchedule calldata newSchedule) external onlyOwner {
        feeSchedule = newSchedule;
        emit FeeScheduleUpdated(newSchedule);
    }

    /// @notice Calculate fee for relaying a packet.
    function calculateRelayFee(bytes calldata data) public view returns (uint256) {
        return feeSchedule.relayBase + feeSchedule.relayPerByte * data.length;
    }

    /// @notice Calculate fee for acknowledgement.
    function calculateAckFee() public view returns (uint256) {
        return feeSchedule.ackBase;
    }

    /// @notice Calculate fee for timeout.
    function calculateTimeoutFee() public view returns (uint256) {
        return feeSchedule.timeoutBase;
    }

    /// @notice Pay relay fee and register the relayer for a packet relay.
    /// Must be called with msg.value covering the fee.
    function payForRelay(
        bytes32 channelId,
        uint64 sequence,
        bytes calldata data,
        address relayer
    ) external payable {
        uint256 fee = calculateRelayFee(data);
        if (msg.value < fee) revert FeeTooLow(fee, msg.value);

        bytes32 key = keccak256(abi.encodePacked(channelId, sequence));
        if (relayClaims[key][sequence].claimed) revert AlreadyClaimed(sequence, channelId);

        relayClaims[key][sequence] = Claim({claimed: true, claimant: relayer, amount: fee});
        pendingFees[relayer] += fee;

        emit FeePaid(relayer, channelId, sequence, FeeTier.RELAY, fee);
    }

    /// @notice Pay fee for acknowledgement.
    function payForAck(bytes32 channelId, uint64 sequence, address relayer) external payable {
        uint256 fee = calculateAckFee();
        if (msg.value < fee) revert FeeTooLow(fee, msg.value);

        bytes32 key = keccak256(abi.encodePacked(channelId, sequence));
        if (ackClaims[key][sequence].claimed) revert AlreadyClaimed(sequence, channelId);

        ackClaims[key][sequence] = Claim({claimed: true, claimant: relayer, amount: fee});
        pendingFees[relayer] += fee;

        emit FeePaid(relayer, channelId, sequence, FeeTier.ACK, fee);
    }

    /// @notice Pay fee for timeout.
    function payForTimeout(bytes32 channelId, uint64 sequence, address relayer) external payable {
        uint256 fee = calculateTimeoutFee();
        if (msg.value < fee) revert FeeTooLow(fee, msg.value);

        bytes32 key = keccak256(abi.encodePacked(channelId, sequence));
        if (timeoutClaims[key][sequence].claimed) revert AlreadyClaimed(sequence, channelId);

        timeoutClaims[key][sequence] = Claim({claimed: true, claimant: relayer, amount: fee});
        pendingFees[relayer] += fee;

        emit FeePaid(relayer, channelId, sequence, FeeTier.TIMEOUT, fee);
    }

    /// @notice Withdraw accumulated fees.
    function withdraw() external {
        uint256 amount = pendingFees[msg.sender];
        if (amount == 0) revert InsufficientFee();
        pendingFees[msg.sender] = 0;
        (bool sent,) = payable(msg.sender).call{value: amount}("");
        require(sent, "Fee transfer failed");
        emit FeeWithdrawn(msg.sender, amount);
    }

    /// @notice Withdraw fees for a specific relayer (owner only).
    function withdrawFor(address relayer) external onlyOwner {
        uint256 amount = pendingFees[relayer];
        if (amount == 0) revert InsufficientFee();
        pendingFees[relayer] = 0;
        (bool sent,) = payable(relayer).call{value: amount}("");
        require(sent, "Fee transfer failed");
        emit FeeWithdrawn(relayer, amount);
    }
}
