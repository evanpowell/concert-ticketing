output "public_ip" {
  value = oci_core_instance.app.public_ip
}

output "ssh_command" {
  value = "ssh ubuntu@${oci_core_instance.app.public_ip}"
}

output "availability_domain" {
  value = oci_core_instance.app.availability_domain
}
