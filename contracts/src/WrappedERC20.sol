// SPDX-License-Identifier: MIT
pragma solidity ^0.8.28;

/// @title WrappedERC20
/// @notice Minimal ERC20 that can be minted/burned by a bridge contract.
contract WrappedERC20 {
    string public name;
    string public symbol;
    uint8 public immutable decimals;
    address public immutable bridge;

    uint256 public totalSupply;
    mapping(address => uint256) public balanceOf;
    mapping(address => mapping(address => uint256)) public allowance;

    event Transfer(address indexed from, address indexed to, uint256 value);
    event Approval(address indexed owner, address indexed spender, uint256 value);

    error NotBridge();
    error InsufficientBalance();
    error InsufficientAllowance();

    modifier onlyBridge() {
        if (msg.sender != bridge) revert NotBridge();
        _;
    }

    constructor(
        string memory _name,
        string memory _symbol,
        uint8 _decimals,
        address _bridge
    ) {
        name = _name;
        symbol = _symbol;
        decimals = _decimals;
        bridge = _bridge;
    }

    function mint(address to, uint256 amount) external onlyBridge {
        totalSupply += amount;
        balanceOf[to] += amount;
        emit Transfer(address(0), to, amount);
    }

    function burn(address from, uint256 amount) external onlyBridge {
        if (balanceOf[from] < amount) revert InsufficientBalance();
        totalSupply -= amount;
        balanceOf[from] -= amount;
        emit Transfer(from, address(0), amount);
    }

    function transfer(address to, uint256 amount) external returns (bool) {
        return _transfer(msg.sender, to, amount);
    }

    function transferFrom(address from, address to, uint256 amount) external returns (bool) {
        uint256 allowed = allowance[from][msg.sender];
        if (allowed != type(uint256).max) {
            if (allowed < amount) revert InsufficientAllowance();
            allowance[from][msg.sender] = allowed - amount;
        }
        return _transfer(from, to, amount);
    }

    function approve(address spender, uint256 amount) external returns (bool) {
        allowance[msg.sender][spender] = amount;
        emit Approval(msg.sender, spender, amount);
        return true;
    }

    function _transfer(address from, address to, uint256 amount) internal returns (bool) {
        if (from == address(0) || to == address(0)) revert();
        if (balanceOf[from] < amount) revert InsufficientBalance();
        balanceOf[from] -= amount;
        balanceOf[to] += amount;
        emit Transfer(from, to, amount);
        return true;
    }
}
