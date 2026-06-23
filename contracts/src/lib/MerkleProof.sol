// SPDX-License-Identifier: MIT
pragma solidity ^0.8.28;

/// @title MerkleProof
/// @notice Merkle proof verification for IBC state proofs.
/// Supports both inclusion and non-inclusion proofs.
library MerkleProof {
    error InvalidProofLength();
    error InvalidProofOrder();

    /// @notice Verify a Merkle inclusion proof using SHA256 hashing (IBC standard).
    /// @param root Merkle root.
    /// @param leaf Leaf hash.
    /// @param proof Sibling hashes from leaf to root.
    /// @return true if proof is valid.
    function verifyInclusion(
        bytes32 root,
        bytes32 leaf,
        bytes32[] calldata proof
    ) internal pure returns (bool) {
        bytes32 hash = leaf;
        for (uint256 i = 0; i < proof.length; i++) {
            hash = hash < proof[i]
                ? sha256(abi.encodePacked(hash, proof[i]))
                : sha256(abi.encodePacked(proof[i], hash));
        }
        return hash == root;
    }

    /// @notice Verify a Merkle inclusion proof using Keccak256 hashing.
    function verifyInclusionKeccak(
        bytes32 root,
        bytes32 leaf,
        bytes32[] calldata proof
    ) internal pure returns (bool) {
        bytes32 hash = leaf;
        for (uint256 i = 0; i < proof.length; i++) {
            hash = hash < proof[i]
                ? keccak256(abi.encodePacked(hash, proof[i]))
                : keccak256(abi.encodePacked(proof[i], hash));
        }
        return hash == root;
    }

    /// @notice Verify a Merkle non-inclusion proof for a sorted tree.
    /// Proves that a value is not present between two adjacent leaves.
    /// @param root Merkle root.
    /// @param value The value whose absence to prove.
    /// @param leftProof Proof to the immediate left leaf.
    /// @param rightProof Proof to the immediate right leaf.
    /// @param leftKey Key of the left neighbor.
    /// @param leftValue Value of the left neighbor.
    /// @param rightKey Key of the right neighbor.
    /// @param rightValue Value of the right neighbor.
    function verifyNonInclusion(
        bytes32 root,
        bytes32 value,
        bytes32[] calldata leftProof,
        bytes32[] calldata rightProof,
        bytes32 leftKey,
        bytes32 leftValue,
        bytes32 rightKey,
        bytes32 rightValue
    ) internal pure returns (bool) {
        // Verify left neighbor exists
        bytes32 leftLeaf = keccak256(abi.encodePacked(leftKey, leftValue));
        bytes32 leftRoot = leftLeaf;
        for (uint256 i = 0; i < leftProof.length; i++) {
            leftRoot = leftRoot < leftProof[i]
                ? keccak256(abi.encodePacked(leftRoot, leftProof[i]))
                : keccak256(abi.encodePacked(leftProof[i], leftRoot));
        }

        // Verify right neighbor exists
        bytes32 rightLeaf = keccak256(abi.encodePacked(rightKey, rightValue));
        bytes32 rightRoot = rightLeaf;
        for (uint256 i = 0; i < rightProof.length; i++) {
            rightRoot = rightRoot < rightProof[i]
                ? keccak256(abi.encodePacked(rightRoot, rightProof[i]))
                : keccak256(abi.encodePacked(rightProof[i], rightRoot));
        }

        // Both must reconstruct the same root
        if (leftRoot != rightRoot) return false;
        if (leftRoot != root) return false;

        // Value must be between left and right neighbors
        if (!(leftKey < value && value < rightKey)) return false;
        if (leftKey >= rightKey) return false;

        return true;
    }

    /// @notice Compute the leaf hash for a key-value pair.
    function leafHash(bytes32 key, bytes32 value) internal pure returns (bytes32) {
        return keccak256(abi.encodePacked(key, value));
    }

    /// @notice Compute the root from a set of leaves using a sorted Merkle tree.
    function computeRoot(bytes32[] memory leaves) internal pure returns (bytes32) {
        if (leaves.length == 0) return bytes32(0);
        if (leaves.length == 1) return leaves[0];

        uint256 len = leaves.length;
        while (len > 1) {
            uint256 newLen = (len + 1) / 2;
            for (uint256 i = 0; i < newLen; i++) {
                uint256 j = i * 2;
                if (j + 1 < len) {
                    leaves[i] = leaves[j] < leaves[j + 1]
                        ? keccak256(abi.encodePacked(leaves[j], leaves[j + 1]))
                        : keccak256(abi.encodePacked(leaves[j + 1], leaves[j]));
                } else {
                    leaves[i] = leaves[j];
                }
            }
            len = newLen;
        }
        return leaves[0];
    }
}
