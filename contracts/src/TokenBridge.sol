// SPDX-License-Identifier: MIT
pragma solidity ^0.8.28;

import {CrossChainMessage} from "./CrossChainMessage.sol";
import {WrappedERC20} from "./WrappedERC20.sol";

/// @title TokenBridge
/// @notice Cross-chain token bridge using IBC + ZKP.
///
/// Flow (Chain A → Chain B):
///   1. User approves and calls bridgeOut(token, amount, destChainId, receiver)
///   2. Bridge locks tokens in vault (native) OR burns wrapped tokens (destination)
///   3. CrossChainMessage emits a packet
///   4. Relayer picks up the event, submits to Chain B with ZKP proof
///   5. Bridge on Chain B mints wrapped tokens OR unlocks native tokens
///
/// Each chain pair has a token mapping:
///   nativeToken → wrappedToken (on the OTHER chain)
contract TokenBridge {
    error NotRelayer();
    error UnsupportedChain(uint256 chainId);
    error ZeroAmount();
    error InsufficientBalance();
    error BridgePaused();
    error TokenNotRegistered();
    error InvalidTransfer();

    address public owner;
    address public relayer;
    CrossChainMessage public messenger;
    bool public paused;

    /// @notice Token mapping: native token → wrapped token on counterparty
    struct TokenPair {
        address nativeToken;     // Token on THIS chain
        uint256 counterpartyChainId;
        address wrappedToken;    // Wrapped token on the OTHER chain
    }

    /// @notice Locked token balance per (token, user)
    mapping(address token => mapping(address user => uint256)) public lockedBalances;

    /// @notice tokenId => TokenPair (both directions)
    mapping(bytes32 pairId => TokenPair) public tokenPairs;

    /// @notice Registered chains
    mapping(uint256 chainId => bool) public supportedChains;

    event TokenRegistered(bytes32 indexed pairId, address nativeToken, uint256 counterpartyChainId, address wrappedToken);
    event BridgeOut(bytes32 indexed pairId, address indexed sender, address indexed receiver, uint256 amount, uint64 sequence);
    event BridgeIn(bytes32 indexed pairId, address indexed receiver, uint256 amount, uint64 sequence);
    event RelayerUpdated(address indexed oldRelayer, address indexed newRelayer);
    event Paused(bool state);

    modifier notPaused() {
        if (paused) revert BridgePaused();
        _;
    }

    modifier onlyRelayer() {
        if (msg.sender != relayer) revert NotRelayer();
        _;
    }

    constructor(address _messenger, address _owner, address _relayer) {
        messenger = CrossChainMessage(_messenger);
        owner = _owner;
        relayer = _relayer;
    }

    /// @notice Register a token pair for bridging.
    function registerToken(
        address nativeToken,
        uint256 counterpartyChainId,
        address wrappedToken
    ) external {
        if (msg.sender != owner) revert NotRelayer();
        bytes32 pairId = keccak256(abi.encodePacked(nativeToken, counterpartyChainId));
        tokenPairs[pairId] = TokenPair({
            nativeToken: nativeToken,
            counterpartyChainId: counterpartyChainId,
            wrappedToken: wrappedToken
        });
        supportedChains[counterpartyChainId] = true;
        emit TokenRegistered(pairId, nativeToken, counterpartyChainId, wrappedToken);
    }

    /// @notice Update relayer address.
    function setRelayer(address newRelayer) external {
        if (msg.sender != owner) revert NotRelayer();
        emit RelayerUpdated(relayer, newRelayer);
        relayer = newRelayer;
    }

    /// @notice Pause/unpause the bridge.
    function setPaused(bool state) external {
        if (msg.sender != owner) revert NotRelayer();
        paused = state;
        emit Paused(state);
    }

    /// @notice Bridge tokens from this chain to the counterparty.
    /// Locks native tokens and emits an IBC packet.
    function bridgeOut(
        address token,
        uint256 amount,
        uint256 destChainId,
        address receiver,
        string calldata destPort,
        bytes32 destChannel,
        uint64 timeoutTimestamp
    ) external notPaused returns (uint64 sequence) {
        if (amount == 0) revert ZeroAmount();

        bytes32 pairId = keccak256(abi.encodePacked(token, destChainId));
        TokenPair storage pair = tokenPairs[pairId];
        if (pair.nativeToken == address(0)) revert TokenNotRegistered();

        // Lock tokens from sender
        if (pair.nativeToken == address(0)) {
            // Native chain currency (ETH)
            if (msg.value < amount) revert InsufficientBalance();
        } else {
            // ERC20: transfer from sender to this contract
            bool ok = WrappedERC20(token).transferFrom(msg.sender, address(this), amount);
            if (!ok) revert InsufficientBalance();
        }

        lockedBalances[token][msg.sender] += amount;

        // Encode bridge operation as IBC packet data
        bytes memory packetData = abi.encode(
            uint8(1), // op: BRIDGE_OUT
            token,
            receiver,
            amount
        );

        sequence = messenger.sendMessage(
            "bridge",
            bytes32(uint256(destChainId)),
            destPort,
            destChannel,
            address(this),
            packetData,
            timeoutTimestamp
        );

        emit BridgeOut(pairId, msg.sender, receiver, amount, sequence);
    }

    /// @notice Complete a bridge-in operation (called by relayer after ZKP verification).
    /// Mints wrapped tokens on this chain.
    function bridgeIn(
        bytes32 pairId,
        address receiver,
        uint256 amount,
        uint64 sequence
    ) external onlyRelayer notPaused {
        TokenPair storage pair = tokenPairs[pairId];
        if (pair.wrappedToken == address(0)) revert TokenNotRegistered();

        WrappedERC20(pair.wrappedToken).mint(receiver, amount);

        emit BridgeIn(pairId, receiver, amount, sequence);
    }

    /// @notice Complete a bridge-in operation (native token unlock).
    /// Called when bridging FROM the wrapped chain back TO the native chain.
    function unlock(
        bytes32 pairId,
        address receiver,
        address token,
        uint256 amount,
        uint64 sequence
    ) external onlyRelayer notPaused {
        TokenPair storage pair = tokenPairs[pairId];
        if (pair.nativeToken == address(0)) revert TokenNotRegistered();

        if (lockedBalances[token][receiver] < amount) revert InsufficientBalance();
        lockedBalances[token][receiver] -= amount;

        if (pair.nativeToken == address(0)) {
            (bool sent,) = payable(receiver).call{value: amount}("");
            if (!sent) revert InvalidTransfer();
        } else {
            bool ok = WrappedERC20(token).transfer(receiver, amount);
            if (!ok) revert InvalidTransfer();
        }

        emit BridgeIn(pairId, receiver, amount, sequence);
    }

    /// @notice Query locked balance for a token-user pair.
    function getLockedBalance(address token, address user) external view returns (uint256) {
        return lockedBalances[token][user];
    }

    /// @notice Get token pair ID.
    function getPairId(address nativeToken, uint256 counterpartyChainId)
        external
        pure
        returns (bytes32)
    {
        return keccak256(abi.encodePacked(nativeToken, counterpartyChainId));
    }
}
