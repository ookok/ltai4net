// SPDX-License-Identifier: MIT
pragma solidity ^0.8.28;

import {IbcRelay} from "../src/IbcRelay.sol";
import {MockLightClient} from "../src/test/MockLightClient.sol";
import {MockZKPVerify} from "../src/test/MockZKPVerify.sol";

contract IbcRelayTest {
    MockZKPVerify public zkp;
    MockLightClient public lightClient;
    IbcRelay public ibc;
    bytes32 constant PORT_CHANNEL = keccak256("transfer/channel-0");
    bytes32 constant CHANNEL_0 = keccak256("channel-0");
    string constant PORT_ID = "transfer";

    event Assertion(bool ok, string message);

    function setUp() public {
        zkp = new MockZKPVerify();
        zkp.setAlwaysPass(true);

        bytes32 vkId = keccak256("test");
        ZKPVerify.VerifyingKey memory vk = _dummyVK(2);
        zkp.registerVK(vkId, vk);

        lightClient = new MockLightClient(
            address(zkp), vkId, vkId, address(this), 86400 * 14, 86400 * 21, 600
        );

        ibc = new IbcRelay(address(lightClient), address(this), address(this));
        ibc.allowPort(PORT_ID);

        ibc.connectionOpenTry(
            keccak256("connection-0"),
            "client-0",
            "counterparty-client-0",
            keccak256("counterparty-connection-0")
        );

        ibc.channelOpenTry(
            CHANNEL_0,
            PORT_ID,
            1, // ORDERED
            "counterparty-transfer",
            keccak256("counterparty-channel-0"),
            "ics20-1",
            keccak256("connection-0")
        );
    }

    function testSendPacket() public {
        bytes memory data = abi.encode("test-message");
        IbcRelay.Height memory height = IbcRelay.Height(0, 0);

        uint64 seq = ibc.sendPacket(PORT_ID, CHANNEL_0, "dest-port", keccak256("dest-channel"), data, height, 0);
        assert(seq == 0);
    }

    function testSendPacketIncrementsSequence() public {
        bytes memory data = abi.encode("test");
        IbcRelay.Height memory height = IbcRelay.Height(0, 0);

        uint64 seq1 = ibc.sendPacket(PORT_ID, CHANNEL_0, "dest", keccak256("dc"), data, height, 0);
        uint64 seq2 = ibc.sendPacket(PORT_ID, CHANNEL_0, "dest", keccak256("dc"), data, height, 0);
        assert(seq2 == seq1 + 1);
    }

    function testRecvPacket() public {
        bytes memory data = abi.encode("test");
        IbcRelay.Packet memory packet = IbcRelay.Packet({
            sequence: 0,
            sourcePort: "counterparty-transfer",
            sourceChannel: keccak256("counterparty-channel-0"),
            destPort: PORT_ID,
            destChannel: CHANNEL_0,
            data: data,
            timeoutHeight: IbcRelay.Height(0, 0),
            timeoutTimestamp: 0
        });

        ZKPVerify.Proof memory proof = _dummyProof();
        uint256[] memory inputs = new uint256[](1);
        inputs[0] = 100;
        bytes32[] memory merkleProof;

        ibc.recvPacket(packet, proof, inputs, merkleProof);
    }

    function testRecvPacketDuplicate() public {
        bytes memory data = abi.encode("dup");
        IbcRelay.Packet memory packet = _makePacket(data);

        ibc.recvPacket(packet, _dummyProof(), new uint256[](1), new bytes32[](0));

        bool failed = false;
        try {
            ibc.recvPacket(packet, _dummyProof(), new uint256[](1), new bytes32[](0));
        } catch {
            failed = true;
        }
        assert(failed);
    }

    function testChannelClose() public {
        ibc.channelClose(PORT_ID, CHANNEL_0);

        bool failed = false;
        bytes memory data = abi.encode("test");
        IbcRelay.Height memory height = IbcRelay.Height(0, 0);
        try {
            ibc.sendPacket(PORT_ID, CHANNEL_0, "dest", keccak256("dc"), data, height, 0);
        } catch {
            failed = true;
        }
        assert(failed);
    }

    function testSendPacketUnauthorizedPort() public {
        bool failed = false;
        try {
            ibc.sendPacket("unauthorized", CHANNEL_0, "dest", keccak256("dc"), new bytes(0),
                IbcRelay.Height(0, 0), 0);
        } catch {
            failed = true;
        }
        assert(failed);
    }

    function _makePacket(bytes memory data) internal view returns (IbcRelay.Packet memory) {
        return IbcRelay.Packet({
            sequence: 0,
            sourcePort: "counterparty-transfer",
            sourceChannel: keccak256("counterparty-channel-0"),
            destPort: PORT_ID,
            destChannel: CHANNEL_0,
            data: data,
            timeoutHeight: IbcRelay.Height(0, 0),
            timeoutTimestamp: 0
        });
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
