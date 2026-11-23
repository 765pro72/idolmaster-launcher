"""Generate the launcher TLS files on the machine that will use them."""

from datetime import datetime, timedelta, timezone
import os
from pathlib import Path
import tempfile

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID


def generate(root):
    root = Path(root)
    certs = root / "certs"
    ca = certs / "ca.pem"
    paths = (
        certs / "chain.pem", certs / "leaf.key",
        root / "leaf_c2c8_chain.pem", root / "leaf_c2c8.key", ca,
    )
    if all(path.is_file() for path in paths):
        return

    template = x509.load_pem_x509_certificate((certs / "names-template.pem").read_bytes())
    names = template.extensions.get_extension_for_class(x509.SubjectAlternativeName).value
    now = datetime.now(timezone.utc)
    start, end = now - timedelta(days=1), now + timedelta(days=825)
    root_key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    leaf_key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    root_name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "GKM Local Launcher CA")])
    leaf_name = template.subject

    def builder(subject, issuer, key):
        return (x509.CertificateBuilder().subject_name(subject).issuer_name(issuer)
                .public_key(key.public_key()).serial_number(x509.random_serial_number())
                .not_valid_before(start).not_valid_after(end))

    root_cert = (builder(root_name, root_name, root_key)
                 .add_extension(x509.BasicConstraints(ca=True, path_length=0), critical=True)
                 .add_extension(x509.KeyUsage(True, False, False, False, False, True, True, False, False),
                                critical=True)
                 .sign(root_key, hashes.SHA256()))
    leaf_cert = (builder(leaf_name, root_name, leaf_key)
                 .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
                 .add_extension(names, critical=False)
                 .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), critical=False)
                 .sign(root_key, hashes.SHA256()))
    ca_bytes = root_cert.public_bytes(serialization.Encoding.PEM)
    chain = leaf_cert.public_bytes(serialization.Encoding.PEM) + ca_bytes
    private = leaf_key.private_bytes(
        serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()
    )
    for path, data in zip(paths, (chain, private, chain, private, ca_bytes)):
        with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as stream:
            pending = Path(stream.name)
            stream.write(data)
        try:
            os.replace(pending, path)
        finally:
            pending.unlink(missing_ok=True)


if __name__ == "__main__":
    generate(Path(__file__).resolve().parent)
