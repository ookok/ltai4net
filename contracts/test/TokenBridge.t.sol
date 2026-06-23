// SPDX-License-Identifier: MIT
pragma solidity ^0.8.28;

import {TokenBridge} from "../src/TokenBridge.sol";
import {WrappedERC20} from "../src/WrappedERC20.sol";
import {CrossChainMessage} from "../src/CrossChainMessage.sol";
import {IbcRelay} from "../src/IbcRelay.sol";
import {MockLightClient} from "../src/test/MockLightClient.sol";
import {MockZKPVerify} from "../src/test/MockZKPVerify.sol";

contract TokenBridgeTest {
    MockZKPVerify public zkp;
    MockLightClient public lightClient;
    IbcRelay public ibc;
    CrossChainMessage public messenger;
    TokenBridge public bridge;
    WrappedERC20 public nativeToken;
    WrappedERC20 public wrappedToken;

    address constant RELAYER = address(0x100);
    address constant USER = address(0x200);
    uint256 constant CHAIN_A = 1;
    uint256 constant CHAIN_B = 2;
    bytes32 constant CHANNEL_ID = keccak256("bridge-channel");

    event Assertion(bool ok, string message);

    function setUp() public {
        // --- Setup IBC infrastructure ---
        zkp = new MockZKPVerify();
        zkp.setAlwaysPass(true);

        bytes32 vkId = keccak256("bridge-vk");
        ZKPVerify.VerifyingKey memory vk = _dummyVK(2);
        zkp.registerVK(vkId, vk);

        lightClient = new MockLightClient(
            address(zkp), vkId, vkId, address(this), 86400 * 14, 86400 * 21, 600
        );

        ibc = new IbcRelay(address(lightClient), address(this), RELAYER);
        ibc.allowPort("bridge");

        ibc.connectionOpenTry(
            keccak256("conn-0"), "client-0", "counterparty-client-0",
            keccak256("counterparty-conn-0")
        );

        ibc.channelOpenTry(
            bytes32(uint256(CHAIN_B)), "bridge", 0, "counterparty-bridge",
            bytes32(uint256(CHAIN_A)), "ics20-1", keccak256("conn-0")
        );

        // --- Deploy bridge ---
        messenger = new CrossChainMessage(address(ibc));
        bridge = new TokenBridge(address(messenger), address(this), RELAYER);

        // --- Deploy tokens ---
        nativeToken = new WrappedERC20("Native", "NAT", 18, address(bridge));
        wrappedToken = new WrappedERC20("Wrapped", "wNAT", 18, address(bridge));

        // Register token pair: native on Chain A → wrapped on Chain B
        bridge.registerToken(address(nativeToken), CHAIN_B, address(wrappedToken));

        // Mint some native tokens to user
        nativeToken.mint(USER, 1000 ether);

        // User approves bridge
        vm.startPrank(USER);
        nativeToken.approve(address(bridge), 1000 ether);
        vm.stopPrank();
    }

    function testBridgeOutLockTokens() public {
        vm.prank(USER);
        bridge.bridgeOut(
            address(nativeToken), 100 ether, CHAIN_B, address(0x300),
            "counterparty-bridge", bytes32(uint256(CHAIN_A)), block.timestamp + 3600
        );

        assert(bridge.getLockedBalance(address(nativeToken), USER) == 100 ether);
        assert(nativeToken.balanceOf(USER) == 900 ether);
    }

    function testBridgeInMintsWrapped() public {
        bytes32 pairId = bridge.getPairId(address(nativeToken), CHAIN_B);

        vm.prank(RELAYER);
        bridge.bridgeIn(pairId, USER, 100 ether, 1);

        assert(wrappedToken.balanceOf(USER) == 100 ether);
    }

    function testBridgeInUnlockNative() public {
        // First lock tokens
        vm.prank(USER);
        bridge.bridgeOut(
            address(nativeToken), 50 ether, CHAIN_B, address(0x300),
            "counterparty-bridge", bytes32(uint256(CHAIN_A)), block.timestamp + 3600
        );

        // Then unlock (incoming from Chain B)
        bytes32 pairId = bridge.getPairId(address(nativeToken), CHAIN_B);
        vm.prank(RELAYER);
        bridge.unlock(pairId, USER, address(nativeToken), 30 ether, 1);

        assert(bridge.getLockedBalance(address(nativeToken), USER) == 20 ether);
        assert(nativeToken.balanceOf(USER) == 980 ether); // 1000 - 50 + 30
    }

    function testBridgeOutZeroAmount() public {
        vm.prank(USER);
        bool failed = false;
        try {
            bridge.bridgeOut(
                address(nativeToken), 0, CHAIN_B, address(0x300),
                "counterparty-bridge", bytes32(uint256(CHAIN_A)), 0
            );
        } catch {
            failed = true;
        }
        assert(failed);
    }

    function testBridgeOutUnregisteredToken() public {
        address fakeToken = address(0xdead);
        vm.prank(USER);
        bool failed = false;
        try {
            bridge.bridgeOut(
                fakeToken, 100, CHAIN_B, address(0x300),
                "counterparty-bridge", bytes32(uint256(CHAIN_A)), 0
            );
        } catch {
            failed = true;
        }
        assert(failed);
    }

    function testPausePreventsBridgeOut() public {
        bridge.setPaused(true);

        vm.prank(USER);
        bool failed = false;
        try {
            bridge.bridgeOut(
                address(nativeToken), 1, CHAIN_B, address(0x300),
                "counterparty-bridge", bytes32(uint256(CHAIN_A)), 0
            );
        } catch {
            failed = true;
        }
        assert(failed);
    }

    function testUnpauseResumesBridge() public {
        bridge.setPaused(true);
        bridge.setPaused(false);

        vm.prank(USER);
        bridge.bridgeOut(
            address(nativeToken), 1, CHAIN_B, address(0x300),
            "counterparty-bridge", bytes32(uint256(CHAIN_A)), block.timestamp + 3600
        );
    }

    function testOnlyRelayerCanBridgeIn() public {
        bytes32 pairId = bridge.getPairId(address(nativeToken), CHAIN_B);
        vm.prank(USER);
        bool failed = false;
        try {
            bridge.bridgeIn(pairId, USER, 100, 1);
        } catch {
            failed = true;
        }
        assert(failed);
    }

    function testFullRoundTrip() public {
        // Bridge OUT: lock native, emit IBC packet
        vm.prank(USER);
        uint64 seq = bridge.bridgeOut(
            address(nativeToken), 42 ether, CHAIN_B, address(0x300),
            "counterparty-bridge", bytes32(uint256(CHAIN_A)), block.timestamp + 3600
        );

        // Verify tokens locked
        assert(nativeToken.balanceOf(USER) == 1000 ether - 42 ether);
        assert(nativeToken.balanceOf(address(bridge)) == 42 ether);

        // Bridge IN (on counterparty): mint wrapped
        bytes32 pairId = bridge.getPairId(address(nativeToken), CHAIN_B);
        vm.prank(RELAYER);
        bridge.bridgeIn(pairId, address(0x300), 42 ether, seq);

        // Verify wrapped minted
        assert(wrappedToken.balanceOf(address(0x300)) == 42 ether);

        // Bridge BACK: unlock native, burn wrapped (simplified)
        vm.prank(RELAYER);
        bridge.unlock(pairId, USER, address(nativeToken), 42 ether, seq + 1);

        assert(nativeToken.balanceOf(USER) == 1000 ether); // restored
        assert(bridge.getLockedBalance(address(nativeToken), USER) == 0);
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
}
