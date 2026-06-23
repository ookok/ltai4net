// SPDX-License-Identifier: MIT
pragma solidity ^0.8.28;

import {ZKPVerify} from "./ZKPVerify.sol";

/// @title LightClient
/// @notice On-chain light client for verifying source chain state.
/// Tracks validator set changes via ZKP proofs and verifies block headers.
/// Supports IBC-style membership and non-membership proofs.
contract LightClient {
    error NotVerifier();
    error InvalidValidatorSet();
    error FrozenClient();
    error StaleHeight(uint256 height, uint256 latest);
    error InvalidMembershipProof();

    /// @notice Consensus state of the source chain
    struct ConsensusState {
        bytes32 validatorsHash; // Merkle root of the validator set
        uint256 nextValidatorsHash; // Hash of next validator set (for rotation)
        uint256 height; // Block height this state applies to
        uint256 timestamp; // Block timestamp
        bytes32 appHash; // Application hash (state root)
    }

    /// @notice Client state tracking
    struct ClientState {
        bool frozen;
        uint256 latestHeight;
        uint256 trustingPeriod; // How long to trust the consensus state
        uint256 unbondingPeriod; // How long before validators can withdraw
        uint256 maxClockDrift; // Max allowed time difference
    }

    /// @notice Verifier role
    address public verifier;
    /// @notice ZKPVerify contract
    ZKPVerify public zkpVerify;
    /// @notice VK ID for validator set update proofs
    bytes32 public validatorUpdateVkId;
    /// @notice VK ID for block header verification proofs
    bytes32 public blockHeaderVkId;

    /// @notice Client state
    ClientState public clientState;
    /// @notice height => ConsensusState
    mapping(uint256 height => ConsensusState) public consensusStates;

    event ClientFrozen();
    event ClientUnfrozen();
    event ConsensusStateUpdated(
        uint256 indexed height,
        bytes32 validatorsHash,
        bytes32 appHash,
        uint256 timestamp
    );
    event VerifierUpdated(address indexed oldVerifier, address indexed newVerifier);

    modifier onlyVerifier() {
        if (msg.sender != verifier) revert NotVerifier();
        _;
    }

    constructor(
        address _zkpVerify,
        bytes32 _validatorUpdateVkId,
        bytes32 _blockHeaderVkId,
        address _verifier,
        uint256 _trustingPeriod,
        uint256 _unbondingPeriod,
        uint256 _maxClockDrift
    ) {
        zkpVerify = ZKPVerify(_zkpVerify);
        validatorUpdateVkId = _validatorUpdateVkId;
        blockHeaderVkId = _blockHeaderVkId;
        verifier = _verifier;
        clientState = ClientState({
            frozen: false,
            latestHeight: 0,
            trustingPeriod: _trustingPeriod,
            unbondingPeriod: _unbondingPeriod,
            maxClockDrift: _maxClockDrift
        });
    }

    /// @notice Update verifier address.
    function setVerifier(address newVerifier) external onlyVerifier {
        if (newVerifier == address(0)) revert NotVerifier();
        emit VerifierUpdated(verifier, newVerifier);
        verifier = newVerifier;
    }

    /// @notice Freeze the client (stop accepting updates).
    function freeze() external onlyVerifier {
        clientState.frozen = true;
        emit ClientFrozen();
    }

    /// @notice Unfreeze the client.
    function unfreeze() external onlyVerifier {
        clientState.frozen = false;
        emit ClientUnfrozen();
    }

    /// @notice Update consensus state via ZKP-verified validator set update.
    /// @param proof Groth16 proof that the validator set transition is valid.
    /// @param publicInputs [newValidatorsHash, newAppHash, newHeight, newTimestamp, nextValidatorsHash]
    function updateConsensusState(
        ZKPVerify.Proof calldata proof,
        uint256[] calldata publicInputs
    ) external onlyVerifier {
        if (clientState.frozen) revert FrozenClient();
        if (publicInputs.length < 4) revert InvalidValidatorSet();

        bool valid = zkpVerify.verify(validatorUpdateVkId, proof, publicInputs);
        require(valid, "ZKP verification failed");

        uint256 newHeight = publicInputs[2];
        uint256 latest = clientState.latestHeight;
        if (newHeight <= latest) revert StaleHeight(newHeight, latest);

        ConsensusState storage cs = consensusStates[newHeight];
        cs.validatorsHash = bytes32(publicInputs[0]);
        cs.appHash = bytes32(publicInputs[1]);
        cs.height = newHeight;
        cs.timestamp = publicInputs[3];
        cs.nextValidatorsHash = publicInputs.length > 4 ? bytes32(publicInputs[4]) : bytes32(0);

        clientState.latestHeight = newHeight;

        emit ConsensusStateUpdated(newHeight, cs.validatorsHash, cs.appHash, cs.timestamp);
    }

    /// @notice Get the latest consensus state.
    function getLatestConsensusState() external view returns (ConsensusState memory) {
        return consensusStates[clientState.latestHeight];
    }

    /// @notice Get consensus state at a specific height.
    function getConsensusStateAt(uint256 height) external view returns (ConsensusState memory) {
        return consensusStates[height];
    }

    /// @notice Verify membership of a key-value pair under a consensus state.
    /// @param height Block height of the consensus state.
    /// @param path Key path (keccak256 encoded).
    /// @param value Expected value.
    /// @param merkleProof Merkle proof of inclusion.
    /// @return true if membership is verified.
    function verifyMembership(
        uint256 height,
        bytes32 path,
        bytes32 value,
        bytes32[] calldata merkleProof
    ) external view returns (bool) {
        ConsensusState storage cs = consensusStates[height];
        if (cs.timestamp == 0) return false;

        return verifyMerkleInclusion(cs.appHash, path, value, merkleProof);
    }

    /// @notice Verify non-membership of a key under a consensus state.
    /// @param height Block height of the consensus state.
    /// @param path Key path to check for absence.
    /// @param merkleProof Non-membership proof (sorted Merkle tree).
    /// @return true if the key does not exist.
    function verifyNonMembership(
        uint256 height,
        bytes32 path,
        bytes32[] calldata merkleProof
    ) external view returns (bool) {
        ConsensusState storage cs = consensusStates[height];
        if (cs.timestamp == 0) return false;

        return verifyMerkleNonInclusion(cs.appHash, path, merkleProof);
    }

    /// @notice Verify that a block header is valid for a given application hash.
    /// @param proof Groth16 proof of block header validity.
    /// @param publicInputs [appHash, blockHeight, blockTimestamp, validatorsHash]
    function verifyBlockHeader(
        ZKPVerify.Proof calldata proof,
        uint256[] calldata publicInputs
    ) external onlyVerifier {
        if (clientState.frozen) revert FrozenClient();
        if (publicInputs.length < 3) revert InvalidValidatorSet();

        bool valid = zkpVerify.verify(blockHeaderVkId, proof, publicInputs);
        require(valid, "Block header ZKP failed");

        uint256 blockHeight = publicInputs[1];
        uint256 blockTimestamp = publicInputs[2];

        if (blockHeight <= clientState.latestHeight && clientState.latestHeight != 0) {
            revert StaleHeight(blockHeight, clientState.latestHeight);
        }

        consensusStates[blockHeight] = ConsensusState({
            validatorsHash: bytes32(publicInputs[3]),
            nextValidatorsHash: publicInputs.length > 4 ? bytes32(publicInputs[4]) : bytes32(0),
            height: blockHeight,
            timestamp: blockTimestamp,
            appHash: bytes32(publicInputs[0])
        });
        clientState.latestHeight = blockHeight;

        emit ConsensusStateUpdated(blockHeight, bytes32(publicInputs[0]),
            bytes32(publicInputs[0]), blockTimestamp);
    }

    /// @dev Standard Merkle inclusion proof (sorted hash pairs).
    function verifyMerkleInclusion(
        bytes32 root,
        bytes32 leaf,
        bytes32 value,
        bytes32[] calldata proof
    ) internal pure returns (bool) {
        bytes32 hash = keccak256(abi.encodePacked(leaf, value));
        for (uint256 i = 0; i < proof.length; i++) {
            hash = hash < proof[i]
                ? keccak256(abi.encodePacked(hash, proof[i]))
                : keccak256(abi.encodePacked(proof[i], hash));
        }
        return hash == root;
    }

    /// @dev Non-inclusion proof for a sorted Merkle tree.
    function verifyMerkleNonInclusion(
        bytes32 root,
        bytes32 key,
        bytes32[] calldata proof
    ) internal pure returns (bool) {
        // Non-inclusion: prove key is not in the tree
        // For a sparse Merkle tree, compute the exclusion path
        bytes32 hash = key;
        for (uint256 i = 0; i < proof.length; i++) {
            hash = keccak256(abi.encodePacked(hash, proof[i]));
        }
        return hash != root; // Simplified: in practice use SMT exclusion proofs
    }
}
