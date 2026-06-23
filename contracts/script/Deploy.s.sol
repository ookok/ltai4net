// SPDX-License-Identifier: MIT
pragma solidity ^0.8.28;

import {ZKPVerify} from "../src/ZKPVerify.sol";
import {Anchor} from "../src/Anchor.sol";
import {LightClient} from "../src/LightClient.sol";
import {IbcRelay} from "../src/IbcRelay.sol";
import {RelayerFee} from "../src/RelayerFee.sol";
import {CrossChainMessage} from "../src/CrossChainMessage.sol";

/// @notice Deploy all cross-chain contracts.
/// forge script script/Deploy.sol --rpc-url <RPC> --broadcast --verify
contract DeployScript {
    function run(
        bytes32 stateTransitionVkId,
        bytes32 validatorUpdateVkId,
        bytes32 blockHeaderVkId,
        address verifier,
        address relayer
    ) external returns (address, address, address, address, address, address) {
        // 1. ZKPVerify
        ZKPVerify zkp = new ZKPVerify();

        // 2. Anchor
        Anchor anchor = new Anchor(
            address(zkp),
            stateTransitionVkId,
            verifier,
            1000,  // maxBlockGap
            10     // minConfirmations
        );

        // 3. LightClient
        LightClient lightClient = new LightClient(
            address(zkp),
            validatorUpdateVkId,
            blockHeaderVkId,
            verifier,
            86400 * 14,  // trusting period: 14 days
            86400 * 21,  // unbonding period: 21 days
            600          // max clock drift: 10 min
        );

        // 4. IbcRelay
        IbcRelay ibc = new IbcRelay(
            address(lightClient),
            verifier,
            relayer
        );

        // 5. RelayerFee
        RelayerFee fee = new RelayerFee(
            address(ibc),
            verifier
        );

        // 6. CrossChainMessage
        CrossChainMessage msgLayer = new CrossChainMessage(
            address(ibc)
        );

        return (address(zkp), address(anchor), address(lightClient),
            address(ibc), address(fee), address(msgLayer));
    }
}
