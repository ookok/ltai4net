// SPDX-License-Identifier: MIT
pragma solidity ^0.8.28;

import {ZKPVerify} from "../src/ZKPVerify.sol";
import {MockZKPVerify} from "../src/test/MockZKPVerify.sol";

/// @notice Minimal test contract for ZKPVerify (runs without forge-std).
/// Run with: forge test --match-contract ZKPVerifyTest -vvv
contract ZKPVerifyTest {
    MockZKPVerify public zkp;
    bytes32 constant VK_ID = keccak256("test-circuit");

    event Assertion(bool ok, string message);

    function setUp() public {
        zkp = new MockZKPVerify();
    }

    function testRegisterVK() public {
        ZKPVerify.VerifyingKey memory vk = _dummyVK(1);
        zkp.registerVK(VK_ID, vk);
        assert(zkp.hasVK(VK_ID));
    }

    function testRegisterDuplicateVK() public {
        ZKPVerify.VerifyingKey memory vk = _dummyVK(1);
        zkp.registerVK(VK_ID, vk);
        bool failed = false;
        try {
            zkp.registerVK(VK_ID, vk);
        } catch {
            failed = true;
        }
        assert(failed);
    }

    function testVerifyWithMock() public {
        ZKPVerify.VerifyingKey memory vk = _dummyVK(1);
        zkp.registerVK(VK_ID, vk);
        zkp.setAlwaysPass(true);

        ZKPVerify.Proof memory proof = _dummyProof();
        uint256[] memory inputs = new uint256[](0);
        bool result = zkp.verify(VK_ID, proof, inputs);
        assert(result);
    }

    function testVerifyVKNotFound() public {
        ZKPVerify.Proof memory proof = _dummyProof();
        uint256[] memory inputs = new uint256[](0);
        bool failed = false;
        try {
            zkp.verify(keccak256("nonexistent"), proof, inputs);
        } catch {
            failed = true;
        }
        assert(failed);
    }

    function testBatchVerify() public {
        ZKPVerify.Proof memory proof = _dummyProof();
        uint256[] memory inputs = new uint256[](0);
        bytes32[] memory ids = new bytes32[](1);
        ids[0] = VK_ID;
        ZKPVerify.Proof[] memory proofs = new ZKPVerify.Proof[](1);
        proofs[0] = proof;
        uint256[][] memory allInputs = new uint256[][](1);
        allInputs[0] = inputs;

        // VK not registered yet, batch will catch on verify call
        bool failed = false;
        try {
            zkp.batchVerify(ids, proofs, allInputs);
        } catch {
            failed = true;
        }
        assert(failed);
    }

    function _dummyVK(uint256 inputCount) internal pure returns (ZKPVerify.VerifyingKey memory) {
        ZKPVerify.G1Point[] memory ic = new ZKPVerify.G1Point[](inputCount + 1);
        for (uint256 i = 0; i <= inputCount; i++) {
            ic[i] = ZKPVerify.G1Point({X: uint256(keccak256(abi.encode("ic", i))), Y: 1});
        }
        return ZKPVerify.VerifyingKey({
            alpha: ZKPVerify.G1Point({X: 1, Y: 2}),
            beta: ZKPVerify.G2Point({X: [uint256(1), 2], Y: [uint256(3), 4]}),
            gamma: ZKPVerify.G2Point({X: [uint256(5), 6], Y: [uint256(7), 8]}),
            delta: ZKPVerify.G2Point({X: [uint256(9), 10], Y: [uint256(11), 12]}),
            gammaAbc: ic
        });
    }

    function _dummyProof() internal pure returns (ZKPVerify.Proof memory) {
        return ZKPVerify.Proof({
            a: ZKPVerify.G1Point({X: 1, Y: 2}),
            b: ZKPVerify.G2Point({X: [uint256(3), 4], Y: [uint256(5), 6]}),
            c: ZKPVerify.G1Point({X: 7, Y: 8})
        });
    }
}
