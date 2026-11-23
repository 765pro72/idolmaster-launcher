using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

// Runs before ProcessStartInfo.EnvironmentVariables is first accessed.
// Reads the raw Win32 environment block because case-insensitive managed maps
// can throw while importing duplicated NO_PROXY/no_proxy entries.
public static class GkmEnvironmentRepair
{
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)]
    static extern IntPtr GetEnvironmentStringsW();
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)]
    static extern bool FreeEnvironmentStringsW(IntPtr block);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool SetEnvironmentVariableW(string name, string value);

    public static List<KeyValuePair<string,string>> Snapshot()
    {
        IntPtr block=GetEnvironmentStringsW();
        if(block==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
        List<KeyValuePair<string,string>> rows=new List<KeyValuePair<string,string>>();
        try
        {
            int offset=0;
            while(true)
            {
                string row=Marshal.PtrToStringUni(IntPtr.Add(block,offset));
                if(String.IsNullOrEmpty(row))break;
                offset+=(row.Length+1)*2;
                int split=row.IndexOf('=');
                // Preserve special Win32 =C: working-directory entries.
                if(split<=0)continue;
                rows.Add(new KeyValuePair<string,string>(row.Substring(0,split),row.Substring(split+1)));
            }
        }
        finally{FreeEnvironmentStringsW(block);}
        return rows;
    }

    public static int Repair()
    {
        Dictionary<string,List<KeyValuePair<string,string>>> groups=new Dictionary<string,List<KeyValuePair<string,string>>>(StringComparer.OrdinalIgnoreCase);
        foreach(KeyValuePair<string,string> row in Snapshot())
        {
            List<KeyValuePair<string,string>> group;
            if(!groups.TryGetValue(row.Key,out group)){group=new List<KeyValuePair<string,string>>();groups.Add(row.Key,group);}
            group.Add(row);
        }
        int changed=0;
        foreach(List<KeyValuePair<string,string>> group in groups.Values)
        {
            if(group.Count<2)continue;
            KeyValuePair<string,string> chosen=group[0];
            foreach(KeyValuePair<string,string> row in group)
                if(row.Key==row.Key.ToUpperInvariant()){chosen=row;break;}
            // A Win32 delete can remove only one occurrence from a malformed
            // block. Delete once for each raw occurrence, then retain one.
            foreach(KeyValuePair<string,string> row in group)
                if(!SetEnvironmentVariableW(row.Key,null))throw new System.ComponentModel.Win32Exception();
            if(!SetEnvironmentVariableW(chosen.Key,chosen.Value))throw new System.ComponentModel.Win32Exception();
            changed++;
        }
        return changed;
    }
}
