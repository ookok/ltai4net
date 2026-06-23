// SPDX-License-Identifier: MIT
pragma solidity ^0.8.28;

import {LightClient} from "../LightClient.sol";

/// @title MockLightClient
/// @notice Mock LightClient for testing IbcRelay without real consensus updates.
contract MockLightClient is LightClient {
    /// @notice Pre-configured membership results
    mapping(bytes32 membershipKey => bool) internal _membershipResults;
    mapping(bytes32 nonMembershipKey => bool) internal _nonMembershipResults;
    bool public defaultMembershipResult = true;

    /// @notice Membership key = keccak256(height, path, value)
    function setMembershipResult(uint256 height, bytes32 path, bytes32 value, bool result) external {
        _membershipResults[keccak256(abi.encode(height, path, value))] = result;
    }

    /// @notice Non-membership key = keccak256(height, path)
    function setNonMembershipResult(uint256 height, bytes32 path, bool result) external {
        _nonMembershipResults[keccak256(abi.encode(height, path))] = result;
    }

    /// @notice Set default membership result.
    function setDefaultMembership(bool result) external {
        defaultMembershipResult = result;
    }

    /// @notice Override verifyMembership to use mock results.
    function verifyMembership(
        uint256 height,
        bytes32 path,
        bytes32 value,
        bytes32[] calldata merkleProof
    ) external view override returns (bool) {
        bytes32 key = keccak256(abi.encode(height, path, value));
        if (_membershipResults[key]) return true;
        return defaultMembershipResult;
    }

    /// @notice Override verifyNonMembership to use mock results.
    function verifyNonMembership(
        uint256 height,
        bytes32 path,
        bytes32[] calldata merkleProof
    ) external view override returns (bool) {
        bytes32 key = keccak256(abi.encode(height, path));
        if (_nonMembershipResults[key]) return true;
        return defaultMembershipResult;
    }

    constructor(
        address _zkpVerify,
        bytes32 _validatorUpdateVkId,
        bytes32 _blockHeaderVkId,
        address _verifier,
        uint256 _trustingPeriod,
        uint256 _unbondingPeriod,
        uint256 _maxClockDrift
    ) LightClient(_zkpVerify, _validatorUpdateVkId, _blockHeaderVkId, _verifier,
        _trustingPeriod, _unbondingPeriod, _maxClockDrift) {}
}
