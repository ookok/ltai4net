// SPDX-License-Identifier: MIT
pragma solidity ^0.8.28;

import {LightClient} from "../src/LightClient.sol";
import {MockZKPVerify} from "../src/test/MockZKPVerify.sol";

contract LightClientTest {
    MockZKPVerify public zkp;
    LightClient public lightClient;
    bytes32 constant UPDATE_VK = keccak256("validator-update");
    bytes32 constant HEADER_VK = keccak256("block-header");

    event Assertion(bool ok, string message);

    function setUp() public {
        zkp = new MockZKPVerify();
        ZKPVerify.VerifyingKey memory vk = _dummyVK(4);
        zkp.registerVK(UPDATE_VK, vk);
        zkp.registerVK(HEADER_VK, vk);
        zkp.setAlwaysPass(true);

        lightClient = new LightClient(
            address(zkp),
            UPDATE_VK,
            HEADER_VK,
            address(this),
            86400 * 14,  // trusting period: 14 days
            86400 * 21,  // unbonding period: 21 days
            600          // max clock drift: 10 min
        );
    }

    function testConstructor() public {
        assert(lightClient.verifier() == address(this));
        assert(!lightClient.clientState().frozen);
    }

    function testUpdateConsensusState() public {
        ZKPVerify.Proof memory proof = _dummyProof();
        uint256[] memory inputs = new uint256[](5);
        inputs[0] = uint256(keccak256("validator-hash"));
        inputs[1] = uint256(keccak256("app-hash"));
        inputs[2] = 100; // height
        inputs[3] = block.timestamp;
        inputs[4] = uint256(keccak256("next-validator-hash"));

        lightClient.updateConsensusState(proof, inputs);

        LightClient.ConsensusState memory cs = lightClient.getConsensusStateAt(100);
        assert(cs.height == 100);
        assert(cs.validatorsHash == keccak256("validator-hash"));
        assert(cs.appHash == keccak256("app-hash"));
    }

    function testStaleBlockHeight() public {
        ZKPVerify.Proof memory proof = _dummyProof();
        uint256[] memory inputs = new uint256[](5);
        inputs[2] = 100;
        inputs[3] = block.timestamp;
        lightClient.updateConsensusState(proof, inputs);

        // Try same height again
        bool failed = false;
        try {
            lightClient.updateConsensusState(proof, inputs);
        } catch {
            failed = true;
        }
        assert(failed);
    }

    function testFreezeClient() public {
        lightClient.freeze();
        assert(lightClient.clientState().frozen);

        ZKPVerify.Proof memory proof = _dummyProof();
        uint256[] memory inputs = new uint256[](5);
        inputs[2] = 200;
        inputs[3] = block.timestamp;

        bool failed = false;
        try {
            lightClient.updateConsensusState(proof, inputs);
        } catch {
            failed = true;
        }
        assert(failed);
    }

    function testUnfreeze() public {
        lightClient.freeze();
        lightClient.unfreeze();
        assert(!lightClient.clientState().frozen);
    }

    function testVerifyBlockHeader() public {
        ZKPVerify.Proof memory proof = _dummyProof();
        uint256[] memory inputs = new uint256[](4);
        inputs[0] = uint256(keccak256("app-hash"));
        inputs[1] = 100;
        inputs[2] = block.timestamp;
        inputs[3] = uint256(keccak256("validator-hash"));

        lightClient.verifyBlockHeader(proof, inputs);

        LightClient.ConsensusState memory cs = lightClient.getLatestConsensusState();
        assert(cs.height == 100);
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
