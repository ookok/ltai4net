import base64,sys
with open(sys.argv[1],"r",encoding="utf-8")as f:
    exec(base64.b64decode(f.read()).decode("utf-8"))
