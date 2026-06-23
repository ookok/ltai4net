# ZKP 跨链合约 — 部署指南

## 前提条件

- [Foundry](https://book.getfoundry.sh/) (forge, cast)
- EVM RPC URL（以太坊主网 / Sepolia / 本地 anvil）
- 部署者私钥
- 已生成 Groth16 验证密钥（circom + snarkjs）

## 快速安装 Foundry

```bash
curl -L https://foundry.paradigm.xyz | bash
foundryup
```

## 安装依赖

```bash
cd contracts
forge install foundry-rs/forge-std --no-commit
forge install OpenZeppelin/openzeppelin-contracts --no-commit
```

## 编译

```bash
forge build
```

## 运行测试

```bash
# 全部测试
forge test -vvv

# 仅 ZKPVerify 测试
forge test --match-contract ZKPVerifyTest -vvv

# 仅 IbcRelay 测试
forge test --match-contract IbcRelayTest -vvv

# gas 报告
forge test --gas-report
```

## 架构总览

```
  ┌──────────────────────────────────────────────────┐
  │               TokenBridge (应用层)                │
  │  lock/burn native → IBC 数据包 → mint/unlock    │
  └──────────────────────┬───────────────────────────┘
                         │
                   ┌─────▼──────┐
                   │CrossChainMsg│   ← 高层消息抽象
                   └──────┬─────┘
                          │
┌──────────┐     ┌───────▼───────┐     ┌────────────┐
│ RelayerFee│◄───│   IbcRelay    │◄───│ LightClient │
│ (费用)    │     │ (IBC 中继)    │     │ (轻客户端)  │
└──────────┘     └───────┬───────┘     └──────┬─────┘
                         │                    │
                  ┌──────▼──────┐     ┌───────▼──────┐
                  │    Anchor   │     │  ZKPVerify   │
                  │  (状态锚定)  │────►│  (Groth16)   │
                  └─────────────┘     └──────────────┘
```

## 部署序列

### 第 1 步：部署 ZKPVerify

```bash
forge create src/ZKPVerify.sol:ZKPVerify \
  --rpc-url <RPC> \
  --private-key <KEY>
```

### 第 2 步：部署 Anchor

```bash
forge create src/Anchor.sol:Anchor \
  --constructor-args <ZKP_ADDR> <VK_ID_HEX> <VERIFIER_ADDR> 1000 10 \
  --rpc-url <RPC> \
  --private-key <KEY>
```

### 第 3 步：部署 LightClient

```bash
forge create src/LightClient.sol:LightClient \
  --constructor-args <ZKP_ADDR> <UPDATE_VK> <HEADER_VK> <VERIFIER> 1209600 1814400 600 \
  --rpc-url <RPC> \
  --private-key <KEY>
```

### 第 4 步：部署 IbcRelay

```bash
forge create src/IbcRelay.sol:IbcRelay \
  --constructor-args <LIGHT_CLIENT_ADDR> <VERIFIER> <RELAYER> \
  --rpc-url <RPC> \
  --private-key <KEY>
```

### 第 5 步：注册验证密钥

```bash
# 用 cast 调用 registerVK
cast send <ZKP_ADDR> "registerVK(bytes32,(uint256[2],uint256[2][2],uint256[2][2],uint256[2][2],(uint256,uint256)[]))" \
  <VK_ID> <VK_DATA> \
  --rpc-url <RPC> \
  --private-key <KEY>
```

### 第 6 步：配置锚定链

```bash
cast send <ANCHOR_ADDR> "addSourceChain(uint256)" 1 --rpc-url <RPC> --private-key <KEY>
```

### 第 8 步：部署跨链代币桥

```bash
# 部署 WrappedERC20
forge create src/WrappedERC20.sol:WrappedERC20 \
  --constructor-args "Wrapped NAT" "wNAT" 18 <BRIDGE_ADDR> \
  --rpc-url <RPC> --private-key <KEY>

# 部署 CrossChainMessage
forge create src/CrossChainMessage.sol:CrossChainMessage \
  --constructor-args <IBC_ADDR> \
  --rpc-url <RPC> --private-key <KEY>

# 部署 TokenBridge
forge create src/TokenBridge.sol:TokenBridge \
  --constructor-args <MESSENGER_ADDR> <OWNER> <RELAYER> \
  --rpc-url <RPC> --private-key <KEY>

# 注册代币对
cast send <BRIDGE_ADDR> "registerToken(address,uint256,address)" \
  <NATIVE_TOKEN> <COUNTERPARTY_CHAIN_ID> <WRAPPED_TOKEN> \
  --rpc-url <RPC> --private-key <KEY>
```

### 第 9 步：创建 IBC 通道 for bridge

```bash
cast send <IBC_ADDR> "channelOpenTry(bytes32,string,uint8,string,bytes32,string,bytes32)" \
  <CHAIN_B_ID> "bridge" 0 "counterparty-bridge" <CHAIN_A_ID> "ics20-1" <CONN_ID> \
  --rpc-url <RPC> --private-key <KEY>
```

### 第 7 步：配置 IBC

```bash
# 打开连接
cast send <IBC_ADDR> "connectionOpenTry(bytes32,string,string,bytes32)" \
  <CONN_ID> "client-0" "counterparty-client-0" <COUNTERPARTY_CONN> \
  --rpc-url <RPC> --private-key <KEY>

# 打开通道
cast send <IBC_ADDR> "channelOpenTry(bytes32,string,uint8,string,bytes32,string,bytes32)" \
  <CHANNEL_ID> "transfer" 1 "counterparty-transfer" <COUNTERPARTY_CHAN> "ics20-1" <CONN_ID> \
  --rpc-url <RPC> --private-key <KEY>
```

## 交互示例

### 锚定一个状态

```solidity
// 生成 ZKP 证明（在链下用 circom）
// 调用 anchor
anchor.anchor(
  sourceChainId: 1,
  blockHeight: 1000,
  stateRoot: 0xabcd...,
  prevStateRoot: 0x1234...,
  proof: {a: ..., b: ..., c: ...},
  publicInputs: [validatorSetHash, ...]
);
```

### 跨链桥接代币

```solidity
// Chain A: 锁定 100 NAT → 在 Chain B 铸 100 wNAT
bridge.bridgeOut(
  address(nativeToken), // 原生代币地址
  100 ether,            // 金额
  2,                    // 目标链 ID
  address(receiver),    // 接收者
  "counterparty-bridge", // 目标链 port
  bytes32(uint256(1)),   // 目标链 channel
  block.timestamp + 3600 // 超时时间
);

// Chain B（relayer 调用）：验证 ZKP 后铸 wNAT
bridge.bridgeIn(pairId, receiver, 100 ether, sequence);
```

### 发送 IBC 消息

```solidity
ibcRelay.sendPacket(
  "transfer",
  0xchannelId,
  "dest-port",
  0xdestChannel,
  abi.encode(msg.sender, receiver, payload),
  Height(0, 0),
  block.timestamp + 3600
);
```

## VerifyingKey 格式（Groth16 BN254）

```solidity
struct VerifyingKey {
    G1Point alpha;         // G1 点 (2 × uint256)
    G2Point beta;          // G2 点 (4 × uint256)
    G2Point gamma;         // G2 点 (4 × uint256)
    G2Point delta;         // G2 点 (4 × uint256)
    G1Point[] gammaAbc;    // G1 点数组 (public input 系数)
}

struct Proof {
    G1Point a;  // G1
    G2Point b;  // G2
    G1Point c;  // G1
}
```

## 安全性注意事项

1. **验证密钥管理**：`registerVK` 无权限控制，部署后需通过代理或 Ownable 包装
2. **中继者信任**：`RelayerFee` 不验证消息是否正确中继，依赖链上 LightClient
3. **BN254 配对** ：ZKPVerify 使用 alt_bn128 预编译，gas 约 300k + public input 线性
4. **超时窗口**：IBC 超时时间应足够长以容忍源链出块延迟，但不宜过长
5. **LightClient 冻结**：验证器集被攻破时应立即冻结 LightClient
