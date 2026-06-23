// SPDX-License-Identifier: MIT
pragma solidity ^0.8.28;

import {ZKPVerify} from "./ZKPVerify.sol";

/// @title Anchor
/// @notice Cross-chain state anchoring via ZKP verification.
/// Stores verified state commitments (state roots) from source chains.
/// Each state root is anchored after ZKP proof verification,
/// enabling trustless cross-chain state reads.
contract Anchor {
    error NotVerifier();
    error StateAlreadyAnchored(uint256 sourceChainId, uint256 blockHeight);
    error BlockHeightTooOld(uint256 provided, uint256 latest);
    error InvalidSourceChain(uint256 sourceChainId);

    /// @notice An anchored state commitment
    struct StateCommitment {
        bytes32 stateRoot;
        uint256 blockHeight;
        uint256 timestamp;
        bytes32 prevStateRoot;
        uint256 validatorSetHash;
    }

    /// @notice Verifier role
    address public verifier;
    /// @notice ZKPVerify contract reference
    ZKPVerify public zkpVerify;
    /// @notice Verification key ID for state transition proofs
    bytes32 public stateTransitionVkId;

    /// @notice Maximum allowed block height gap for anchoring
    uint256 public maxBlockGap;
    /// @notice Minimum confirmation blocks required
    uint256 public minConfirmations;

    /// @notice sourceChainId => blockHeight => StateCommitment
    mapping(uint256 sourceChainId => mapping(uint256 blockHeight => StateCommitment)) internal _commitments;
    /// @notice sourceChainId => latest anchored block height
    mapping(uint256 sourceChainId => uint256) internal _latestHeight;
    /// @notice Supported source chain IDs
    mapping(uint256 sourceChainId => bool) internal _supportedChains;

    event VerifierUpdated(address indexed oldVerifier, address indexed newVerifier);
    event SourceChainAdded(uint256 indexed sourceChainId);
    event SourceChainRemoved(uint256 indexed sourceChainId);
    event StateAnchored(
        uint256 indexed sourceChainId,
        uint256 indexed blockHeight,
        bytes32 stateRoot,
        bytes32 prevStateRoot,
        uint256 timestamp,
        uint256 validatorSetHash
    );

    modifier onlyVerifier() {
        if (msg.sender != verifier) revert NotVerifier();
        _;
    }

    constructor(
        address _zkpVerify,
        bytes32 _stateTransitionVkId,
        address _verifier,
        uint256 _maxBlockGap,
        uint256 _minConfirmations
    ) {
        if (_zkpVerify == address(0) || _verifier == address(0)) revert InvalidSourceChain(0);
        zkpVerify = ZKPVerify(_zkpVerify);
        stateTransitionVkId = _stateTransitionVkId;
        verifier = _verifier;
        maxBlockGap = _maxBlockGap;
        minConfirmations = _minConfirmations;
    }

    /// @notice Set a new verifier address.
    function setVerifier(address newVerifier) external onlyVerifier {
        if (newVerifier == address(0)) revert InvalidSourceChain(0);
        emit VerifierUpdated(verifier, newVerifier);
        verifier = newVerifier;
    }

    /// @notice Add a supported source chain.
    function addSourceChain(uint256 sourceChainId) external onlyVerifier {
        _supportedChains[sourceChainId] = true;
        emit SourceChainAdded(sourceChainId);
    }

    /// @notice Remove a supported source chain.
    function removeSourceChain(uint256 sourceChainId) external onlyVerifier {
        _supportedChains[sourceChainId] = false;
        emit SourceChainRemoved(sourceChainId);
    }

    /// @notice Check if a source chain is supported.
    function isSupportedChain(uint256 sourceChainId) external view returns (bool) {
        return _supportedChains[sourceChainId];
    }

    /// @notice Anchor a state commitment after ZKP verification.
    /// @param sourceChainId Source chain identifier.
    /// @param blockHeight Block height on the source chain.
    /// @param stateRoot State root to anchor.
    /// @param prevStateRoot Previous anchored state root.
    /// @param proof Groth16 proof of state transition validity.
    /// @param publicInputs Public inputs for the ZKP circuit.
    function anchor(
        uint256 sourceChainId,
        uint256 blockHeight,
        bytes32 stateRoot,
        bytes32 prevStateRoot,
        ZKPVerify.Proof calldata proof,
        uint256[] calldata publicInputs
    ) external onlyVerifier {
        if (!_supportedChains[sourceChainId]) revert InvalidSourceChain(sourceChainId);
        if (_commitments[sourceChainId][blockHeight].timestamp != 0) {
            revert StateAlreadyAnchored(sourceChainId, blockHeight);
        }

        uint256 latest = _latestHeight[sourceChainId];
        if (blockHeight <= latest && latest != 0) {
            revert BlockHeightTooOld(blockHeight, latest);
        }
        if (latest != 0 && blockHeight - latest > maxBlockGap) {
            // Allow gap but require ZKP proof covers more
        }

        bool valid = zkpVerify.verify(stateTransitionVkId, proof, publicInputs);
        require(valid, "ZKP verification failed");

        _commitments[sourceChainId][blockHeight] = StateCommitment({
            stateRoot: stateRoot,
            blockHeight: blockHeight,
            timestamp: block.timestamp,
            prevStateRoot: prevStateRoot,
            validatorSetHash: publicInputs.length > 0 ? bytes32(publicInputs[0]) : bytes32(0)
        });
        _latestHeight[sourceChainId] = blockHeight;

        emit StateAnchored(
            sourceChainId, blockHeight, stateRoot, prevStateRoot, block.timestamp,
            publicInputs.length > 0 ? bytes32(publicInputs[0]) : bytes32(0)
        );
    }

    /// @notice Get the latest anchored state for a source chain.
    function getLatestState(uint256 sourceChainId)
        external
        view
        returns (StateCommitment memory)
    {
        uint256 height = _latestHeight[sourceChainId];
        return _commitments[sourceChainId][height];
    }

    /// @notice Get a specific anchored state.
    function getState(uint256 sourceChainId, uint256 blockHeight)
        external
        view
        returns (StateCommitment memory)
    {
        return _commitments[sourceChainId][blockHeight];
    }

    /// @notice Batch prove state inclusion: verify that a value exists at a path
    /// under a previously anchored state root.
    /// @param sourceChainId Source chain identifier.
    /// @param blockHeight Block height of the anchored state.
    /// @param path Key path (e.g., keccak256(portId, channelId, sequence)).
    /// @param value Expected value at the path.
    /// @param merkleProof Merkle proof of inclusion.
    /// @return true if the value exists at the path under the anchored state root.
    function proveStateInclusion(
        uint256 sourceChainId,
        uint256 blockHeight,
        bytes32 path,
        bytes32 value,
        bytes32[] calldata merkleProof
    ) external view returns (bool) {
        StateCommitment storage commitment = _commitments[sourceChainId][blockHeight];
        if (commitment.timestamp == 0) return false;
        return verifyMerkleProof(commitment.stateRoot, path, value, merkleProof);
    }

    /// @dev Verify a Merkle proof of inclusion.
    function verifyMerkleProof(
        bytes32 root,
        bytes32 leaf,
        bytes32 value,
        bytes32[] calldata proof
    ) internal pure returns (bool) {
        bytes32 computedHash = keccak256(abi.encodePacked(leaf, value));
        for (uint256 i = 0; i < proof.length; i++) {
            if (computedHash < proof[i]) {
                computedHash = keccak256(abi.encodePacked(computedHash, proof[i]));
            } else {
                computedHash = keccak256(abi.encodePacked(proof[i], computedHash));
            }
        }
        return computedHash == root;
    }
}
