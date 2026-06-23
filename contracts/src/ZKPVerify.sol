// SPDX-License-Identifier: MIT
pragma solidity ^0.8.28;

/// @title ZKPVerify
/// @notice On-chain Groth16 verifier over BN254 (alt_bn128) curve.
/// Uses EVM precompiles: bn256Add (0x06), bn256ScalarMul (0x07), bn256Pairing (0x08).
/// Supports multiple circuits identified by bytes32 verification key ID.
/// @dev Single multicall pairing check; reverts on invalid proof.
contract ZKPVerify {
    error InvalidProof();
    error VKNotFound(bytes32 vkId);
    error VKAlreadyRegistered(bytes32 vkId);

    /// @notice BN254 base field modulus
    uint256 internal constant BN254_FP_MODULUS =
        21888242871839275222246405745257275088696311157297823662689037894645226208583;

    /// @notice G1 point on BN254
    struct G1Point {
        uint256 X;
        uint256 Y;
    }

    /// @notice G2 point on BN254 (each coordinate is an Fp2 element)
    struct G2Point {
        uint256[2] X;
        uint256[2] Y;
    }

    /// @notice Groth16 verification key
    struct VerifyingKey {
        G1Point alpha;
        G2Point beta;
        G2Point gamma;
        G2Point delta;
        G1Point[] gammaAbc;
    }

    /// @notice Groth16 proof
    struct Proof {
        G1Point a;
        G2Point b;
        G1Point c;
    }

    /// @notice Registered verification keys
    mapping(bytes32 vkId => VerifyingKey) internal _vks;
    mapping(bytes32 vkId => bool) internal _vkExists;

    event VKRegistered(bytes32 indexed vkId, uint256 publicInputCount);
    event ProofVerified(bytes32 indexed vkId, address indexed verifier);

    /// @notice Register a verification key for a circuit.
    function registerVK(bytes32 vkId, VerifyingKey calldata vk) external {
        if (_vkExists[vkId]) revert VKAlreadyRegistered(vkId);
        if (vk.gammaAbc.length == 0) revert InvalidProof();
        _vks[vkId] = vk;
        _vkExists[vkId] = true;
        emit VKRegistered(vkId, vk.gammaAbc.length);
    }

    /// @notice Check if a verification key is registered.
    function hasVK(bytes32 vkId) external view returns (bool) {
        return _vkExists[vkId];
    }

    /// @notice Get registered VK public input count.
    function getPublicInputCount(bytes32 vkId) external view returns (uint256) {
        if (!_vkExists[vkId]) revert VKNotFound(vkId);
        return _vks[vkId].gammaAbc.length - 1;
    }

    /// @notice Verify a Groth16 proof against a registered verification key.
    /// Single multicall to bn256Pairing precompile with 4 pairs:
    ///   e(pi_a, pi_b) * e(-alpha, beta) * e(-vkX, gamma) * e(-pi_c, delta) == 1
    function verify(bytes32 vkId, Proof calldata proof, uint256[] calldata publicInputs)
        external
        returns (bool)
    {
        VerifyingKey storage vk = _vks[vkId];
        if (!_vkExists[vkId]) revert VKNotFound(vkId);
        if (publicInputs.length + 1 != vk.gammaAbc.length) revert InvalidProof();

        G1Point memory vkX = accumulateInputs(vk.gammaAbc, publicInputs);

        uint256[24] memory pairs;
        // Pair 0: (pi_a, pi_b)
        pairs[0] = proof.a.X;  pairs[1] = proof.a.Y;
        pairs[2] = proof.b.X[0]; pairs[3] = proof.b.X[1];
        pairs[4] = proof.b.Y[0]; pairs[5] = proof.b.Y[1];
        // Pair 1: (-alpha, beta)
        pairs[6] = negateY(vk.alpha.X, vk.alpha.Y)[0];
        pairs[7] = negateY(vk.alpha.X, vk.alpha.Y)[1];
        pairs[8] = vk.beta.X[0];  pairs[9] = vk.beta.X[1];
        pairs[10] = vk.beta.Y[0]; pairs[11] = vk.beta.Y[1];
        // Pair 2: (-vkX, gamma)
        pairs[12] = negateY(vkX.X, vkX.Y)[0];
        pairs[13] = negateY(vkX.X, vkX.Y)[1];
        pairs[14] = vk.gamma.X[0]; pairs[15] = vk.gamma.X[1];
        pairs[16] = vk.gamma.Y[0]; pairs[17] = vk.gamma.Y[1];
        // Pair 3: (-pi_c, delta)
        pairs[18] = negateY(proof.c.X, proof.c.Y)[0];
        pairs[19] = negateY(proof.c.X, proof.c.Y)[1];
        pairs[20] = vk.delta.X[0]; pairs[21] = vk.delta.X[1];
        pairs[22] = vk.delta.Y[0]; pairs[23] = vk.delta.Y[1];

        (bool success, bytes memory result) = address(0x08).staticcall(abi.encode(pairs));
        if (!success || result.length != 32) revert InvalidProof();
        if (abi.decode(result, (uint256)) == 0) revert InvalidProof();

        emit ProofVerified(vkId, msg.sender);
        return true;
    }

    /// @notice Batch verify multiple proofs.
    function batchVerify(
        bytes32[] calldata vkIds,
        Proof[] calldata proofs,
        uint256[][] calldata publicInputs
    ) external returns (bool[] memory results) {
        uint256 len = vkIds.length;
        if (len != proofs.length || len != publicInputs.length) revert InvalidProof();
        results = new bool[](len);
        for (uint256 i = 0; i < len; i++) {
            results[i] = this.verify(vkIds[i], proofs[i], publicInputs[i]);
        }
    }

    /// @dev Accumulate public inputs: ic[0] + sum(input[i] * ic[i+1])
    function accumulateInputs(G1Point[] storage ic, uint256[] calldata inputs)
        internal
        view
        returns (G1Point memory result)
    {
        result = ic[0];
        for (uint256 i = 0; i < inputs.length; i++) {
            result = bn256Add(result, bn256ScalarMul(ic[i + 1], inputs[i]));
        }
    }

    /// @dev Negate G1 Y coordinate modulo BN254_FP_MODULUS.
    /// Returns [X, negY] as a uint256[2] for inline use.
    function negateY(uint256 x, uint256 y) internal pure returns (uint256[2] memory neg) {
        if (x == 0 && y == 0) return [uint256(0), uint256(0)];
        neg[0] = x;
        neg[1] = BN254_FP_MODULUS - (y % BN254_FP_MODULUS);
    }

    /// @dev G1 addition via bn256Add precompile (address 0x06)
    function bn256Add(G1Point memory a, G1Point memory b) internal view returns (G1Point memory) {
        if (a.X == 0 && a.Y == 0) return b;
        if (b.X == 0 && b.Y == 0) return a;
        uint256[4] memory input = [a.X, a.Y, b.X, b.Y];
        (bool success, bytes memory result) = address(0x06).staticcall(abi.encode(input));
        if (!success) revert InvalidProof();
        return abi.decode(result, (G1Point));
    }

    /// @dev G1 scalar multiplication via bn256ScalarMul precompile (address 0x07)
    function bn256ScalarMul(G1Point storage p, uint256 s) internal view returns (G1Point memory) {
        if (s == 0 || (p.X == 0 && p.Y == 0)) return G1Point(0, 0);
        uint256[3] memory input = [p.X, p.Y, s];
        (bool success, bytes memory result) = address(0x07).staticcall(abi.encode(input));
        if (!success) revert InvalidProof();
        return abi.decode(result, (G1Point));
    }
}
