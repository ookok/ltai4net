// SPDX-License-Identifier: MIT
pragma solidity ^0.8.28;

/// @title IERC20Minimal
/// @notice Minimal ERC20 interface for bridge use.
interface IERC20Minimal {
    function transferFrom(address from, address to, uint256 amount) external returns (bool);
    function transfer(address to, uint256 amount) external returns (bool);
    function balanceOf(address account) external view returns (uint256);
}
