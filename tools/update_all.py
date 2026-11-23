"""兼容旧命令入口；与启动器共用本地同步实现。"""
from local_content_sync import main

if __name__ == '__main__':
    raise SystemExit(main())
