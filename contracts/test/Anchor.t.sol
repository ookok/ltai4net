// SPDX-License-Identifier: MIT
pragma solidity ^0.8.28;

import {Anchor} from "../src/Anchor.sol";
import {MockZKPVerify} from "../src/test/MockZKPVerify.sol";

contract AnchorTest {
    MockZKPVerify public zkp;
    Anchor public anchor;
    bytes32 constant VK_ID = keccak256("anchor-state");
    uint256 constant CHAIN_A = 1;
    uint256 constant GAP = 100;

    event Assertion(bool ok, string message);

    function setUp() public {
        zkp = new MockZKPVerify();
        ZKPVerify.VerifyingKey memory vk = _dummyVK(2);
        zkp.registerVK(VK_ID, vk);
        zkp.setAlwaysPass(true);

        anchor = new Anchor(address(zkp), VK_ID, address(this), GAP, 10);
        anchor.addSourceChain(CHAIN_A);
    }

    function testConstructor() public {
        assert(anchor.zkpVerify() == zkp);
        assert(anchor.verifier() == address(this));
        assert(anchor.maxBlockGap() == GAP);
    }

    function testAnchorState() public {
        ZKPVerify.Proof memory proof = _dummyProof();
        uint256[] memory inputs = new uint256[](2);
        inputs[0] = uint256(keccak256("validator-set"));
        inputs[1] = 0;

        anchor.anchor(CHAIN_A, 100, keccak256("state-root"), bytes32(0), proof, inputs);

        Anchor.StateCommitment memory state = anchor.getLatestState(CHAIN_A);
        assert(state.blockHeight == 100);
        assert(state.stateRoot == keccak256("state-root"));
    }

    function testProveStateInclusion() public {
        ZKPVerify.Proof memory proof = _dummyProof();
        uint256[] memory inputs = new uint256[](2);
        inputs[0] = bytes32(uint256(1));
        inputs[1] = 0;
        anchor.anchor(CHAIN_A, 100, keccak256("state-root"), bytes32(0), proof, inputs);

        bytes32 path = keccak256("test-path");
        bytes32 value = keccak256("test-value");

        bytes32[] memory merkleProof;
        bool result = anchor.proveStateInclusion(CHAIN_A, 100, path, value, merkleProof);
        assert(!result);
    }

    function testUnsupportedChain() public {
        ZKPVerify.Proof memory proof = _dummyProof();
        uint256[] memory inputs = new uint256[](2);
        bool failed = false;
        try {
            anchor.anchor(999, 100, keccak256("root"), bytes32(0), proof, inputs);
        } catch {
            failed = true;
        }
        assert(failed);
    }

    function testDuplicateAnchor() public {
        ZKPVerify.Proof memory proof = _dummyProof();
        uint256[] memory inputs = new uint256[](2);
        inputs[0] = 1;
        inputs[1] = 0;
        anchor.anchor(CHAIN_A, 100, keccak256("root1"), bytes32(0), proof, inputs);

        bool failed = false;
        try {
            anchor.anchor(CHAIN_A, 100, keccak256("root2"), bytes32(0), proof, inputs);
        } catch {
            failed = true;
        }
        assert(failed);
    }

    function testSetVerifier() public {
        address newVerifier = address(0x1234);
        anchor.setVerifier(newVerifier);
        assert(anchor.verifier() == newVerifier);
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
