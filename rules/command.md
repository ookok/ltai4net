# rule: command
layer: L0
quality: 0.40
speed: 0.85
cost: 0.10
description: System command execution — filesystem, git, package management

## keywords
ls
dir
git
dotnet
npm
pip
docker
cd 
rm 
cp 
mv 
mkdir
cat
chmod
curl
wget
ps
kill
grep
find
ssh
scp
brew
scoop
winget
apt
yum
systemctl

## regex
^\s*\$?\s*(git|dotnet|npm|pip|docker|ls|cd|rm|cp|mv)
^\s*(sudo|chmod|chown)
