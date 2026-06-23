// SPDX-License-Identifier: MIT
pragma solidity ^0.8.28;

import {ZKPVerify} from "../ZKPVerify.sol";

/// @title MockZKPVerify
/// @notice Mock ZKP verifier for testing Anchor, LightClient, and IbcRelay.
/// Controller can override verification results in tests.
contract MockZKPVerify is ZKPVerify {
    /// @notice Mock mode: true = always pass, false = use real verification
    bool public alwaysPass = true;

    /// @notice Override verify result when in mock mode
    mapping(bytes32 vkId => mapping(bytes32 proofHash => bool)) public mockResults;

    event MockVerifyCalled(bytes32 indexed vkId, bytes32 indexed proofHash, bool result);

    /// @notice Set mock verification result for a specific (vkId, proof) pair.
    function setMockResult(bytes32 vkId, bytes32 proofHash, bool result) external {
        mockResults[vkId][proofHash] = result;
    }

    /// @notice Toggle always-pass mode.
    function setAlwaysPass(bool pass) external {
        alwaysPass = pass;
    }

    /// @notice Override verify to use mock when alwaysPass or explicit mock result set.
    function verify(bytes32 vkId, Proof calldata proof, uint256[] calldata publicInputs)
        external
        override
        returns (bool)
    {
        bytes32 proofHash = keccak256(abi.encode(proof.a.X, proof.a.Y, proof.c.X, proof.c.Y));
        bool result;
        if (alwaysPass) {
            result = true;
        } else if (mockResults[vkId][proofHash]) {
            result = mockResults[vkId][proofHash];
        } else {
            result = super.verify(vkId, proof, publicInputs);
        }
        emit MockVerifyCalled(vkId, proofHash, result);
        return result;
    }
}
